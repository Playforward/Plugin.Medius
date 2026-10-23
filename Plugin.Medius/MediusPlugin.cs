using Plugin.Medius.Components;
using Plugin.Medius.Extensions;
using Plugin.Medius.Lobby;
using Plugin.Medius.Transport;
using ServerShared.Controllers;
using ServerShared.EventArguments;
using ServerShared.Plugins;
using ServerShared.Server;
using ServerShared.Titles;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace Plugin.Medius;

/// <summary>
/// Sony's SCE-RT "Medius" online stack (MUIS, MAS, MLS, NAT, DME) for the PS2
/// and PS3 games that used it, served on ServerApp listeners. Which games
/// are served comes from title data (<see cref="MediusTitle"/>); nothing
/// here is specific to one game, and what a game added beside Medius is a
/// title extension (<see cref="IMediusTitleExtension"/>).
/// </summary>
public sealed class MediusPlugin : ServerPlugin<MediusSettings>
{
    public override uint Priority => 1;
    public override string Name => "Medius";

    private MediusContext? _context;
    private readonly Dictionary<int, MediusComponent> _tcpComponents = [];
    private readonly Dictionary<int, Action<CoreUdpSession, ReadOnlyMemory<byte>>> _udpHandlers = [];
    private readonly ConcurrentDictionary<Guid, ScertConnection> _connections = new();
    private Timer? _ticker;

    /// <summary>The running plugin's shared state; null until started.</summary>
    internal MediusContext? Context => _context;

    public override void Start()
    {
        List<string> problems = [];
        List<MediusTitle> titles = MediusTitle.LoadAll(TitleRegistry.All, problems);
        foreach (string problem in problems)
            Log.Error("[medius] {Problem}", problem);
        if (titles.Count == 0)
        {
            Log.Warning("[medius] no title with ProtocolFamily \"medius\" in Configs/{File}; not starting", TitleRegistry.ConfigFileName);
            return;
        }

        Dictionary<MediusTitle, IMediusTitleExtension> extensions = [];
        foreach (MediusTitle title in titles.Where(t => !string.IsNullOrEmpty(t.Extension)))
        {
            if (MediusExtensionRegistry.Create(title.Extension) is { } extension)
                extensions[title] = extension;
            else
                Log.Error("[medius] {Title} asks for extension {Extension}, which does not exist (known: {Known})",
                    title, title.Extension, string.Join(", ", MediusExtensionRegistry.Names));
        }

        LobbyState lobby = new(new AccountStore(Config.DataDir));
        _context = new MediusContext(Config, titles, extensions, lobby);
        foreach ((MediusTitle title, IMediusTitleExtension extension) in extensions)
            extension.Initialize(title, _context);

        Dictionary<string, MediusComponent> components = new(StringComparer.OrdinalIgnoreCase)
        {
            [MediusComponentNames.Muis] = new MuisComponent(_context),
            [MediusComponentNames.Mas] = new MasComponent(_context),
            [MediusComponentNames.Mls] = new MlsComponent(_context),
            [MediusComponentNames.Dme] = new DmeComponent(_context),
        };
        foreach (MediusComponent component in extensions.Values.Distinct().SelectMany(e => e.CreateComponents(_context)))
            components[component.Name] = component;

        if (!BindListeners(components))
            return;

        CoreUnsecureSession.OnBytesReceived += OnTcpBytes;
        CoreUnsecureSession.OnDisconnectedEvent += OnTcpDisconnected;
        CoreUdpSession.OnBytesReceived += OnUdpBytes;
        _ticker = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        foreach (MediusTitle title in titles)
            Log.Information("[medius] serving {Title}: app ids {AppIds}{Extension}", title, string.Join("/", title.AppIds),
                extensions.ContainsKey(title) ? $", extension {title.Extension}" : "");
        Log.Information("[medius] listening: {Listeners}; advertising {Address}",
            string.Join(", ", Config.Listeners.Select(l => $"{l.Component} {l.Port}/{(l.Udp ? "udp" : "tcp")}")), _context.PublicAddress);
    }

    public override void Stop()
    {
        CoreUnsecureSession.OnBytesReceived -= OnTcpBytes;
        CoreUnsecureSession.OnDisconnectedEvent -= OnTcpDisconnected;
        CoreUdpSession.OnBytesReceived -= OnUdpBytes;
        _ticker?.Dispose();
        _ticker = null;
        _connections.Clear();
        _tcpComponents.Clear();
        _udpHandlers.Clear();
    }

    /// <summary>
    /// Assigns each configured listener its component, refusing to start if
    /// a component is unknown or ServerApp has no server on that port: a
    /// silently unbound port is a game that times out with nothing logged.
    /// </summary>
    private bool BindListeners(Dictionary<string, MediusComponent> components)
    {
        bool ok = true;
        foreach (MediusListener listener in Config.Listeners)
        {
            bool registered = ServerController.Servers.Any(s => s.Port == listener.Port && s.IsUdp == listener.Udp);
            if (!registered)
            {
                Log.Error("[medius] no {Kind} server on port {Port} for {Component}; add it to ServerAppSettings.json{Udp}",
                    listener.Udp ? "UDP" : "TCP", listener.Port, listener.Component, listener.Udp ? " with UseUDP=true" : "");
                ok = false;
                continue;
            }

            if (listener.Udp)
            {
                if (string.Equals(listener.Component, MediusComponentNames.Nat, StringComparison.OrdinalIgnoreCase))
                    _udpHandlers[listener.Port] = OnNatProbe;
                else if (components.GetValueOrDefault(listener.Component) is DmeComponent dme)
                    _udpHandlers[listener.Port] = (session, data) => dme.HandleDatagram(session, data.Span);
                else
                {
                    Log.Error("[medius] {Component} on UDP port {Port}: only NAT and the DME are served over UDP", listener.Component, listener.Port);
                    ok = false;
                }
                continue;
            }

            if (components.TryGetValue(listener.Component, out MediusComponent? component))
                _tcpComponents[listener.Port] = component;
            else
            {
                Log.Error("[medius] listener {Port} names component {Component}, which is not known (known: {Known})",
                    listener.Port, listener.Component, string.Join(", ", components.Keys));
                ok = false;
            }
        }
        if (Config.Listeners.Any(l => string.Equals(l.Component, MediusComponentNames.Dme, StringComparison.OrdinalIgnoreCase)) &&
            (Config.PortOf(MediusComponentNames.Dme, udp: false) == 0 || Config.PortOf(MediusComponentNames.Dme, udp: true) == 0))
        {
            Log.Error("[medius] the DME needs both a TCP and a UDP listener");
            ok = false;
        }
        return ok;
    }

    private void OnTcpBytes(object? sender, SessionBytesReceivedEventArgs e)
    {
        if (_context is null || sender is not CoreUnsecureSession session || session.Server is not CoreUnsecureServer server)
            return;
        if (!_tcpComponents.TryGetValue(server.Port, out MediusComponent? component))
            return;

        ScertConnection connection = _connections.GetOrAdd(session.Id, _ => new ScertConnection(session, component.Name, _context.ServerKey));
        try
        {
            foreach (var message in connection.Receive(e.Data.Span))
                component.Handle(connection, message);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[medius/{Component}] dropped {Connection} after a message that could not be handled", component.Name, connection);
            _connections.TryRemove(session.Id, out _);
            component.Disconnected(connection);
            connection.Disconnect();
        }
    }

    private void OnTcpDisconnected(object? sender, SessionGuidEventArgs e)
    {
        if (!_connections.TryRemove(e.Id, out ScertConnection? connection))
            return;
        if (_tcpComponents.TryGetValue(connection.LocalPort, out MediusComponent? component))
            component.Disconnected(connection);
        Log.Debug("[medius/{Component}] {Connection} disconnected", connection.Component, connection);
    }

    private void OnUdpBytes(object? sender, SessionBytesReceivedEventArgs e)
    {
        if (sender is not CoreUdpSession session || !_udpHandlers.TryGetValue(session.Server.Port, out var handler))
            return;
        try
        {
            handler(session, e.Data);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[medius] UDP {Port}: datagram from {Remote} could not be handled", session.Server.Port, session.EndPoint);
        }
    }

    /// <summary>
    /// NAT discovery: a 4-byte probe is answered with the address and port
    /// it arrived from (IPv4, then port big-endian), so a client behind NAT
    /// learns its public endpoint. A probe ending 0xD4 is a keep-alive.
    /// </summary>
    private static void OnNatProbe(CoreUdpSession session, ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> probe = data.Span;
        if (probe.Length != 4 || probe[3] == 0xD4 || session.EndPoint is not IPEndPoint remote)
            return;

        byte[] reply = new byte[6];
        remote.Address.MapToIPv4().GetAddressBytes().CopyTo(reply, 0);
        BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(4), (ushort)remote.Port);
        session.SendAsync(reply);
    }

    private void Tick()
    {
        if (_context is null)
            return;
        try
        {
            TimeSpan echoEvery = TimeSpan.FromSeconds(Math.Max(5, Config.EchoIntervalSeconds));
            foreach (ScertConnection connection in _connections.Values)
            {
                if (connection.Title is null || connection.MediusVersion <= 108 || DateTime.UtcNow - connection.LastEchoSent < echoEvery)
                    continue;
                if (_tcpComponents.TryGetValue(connection.LocalPort, out MediusComponent? component))
                    component.SendEcho(connection);
            }

            int swept = _context.Lobby.Sweep(TimeSpan.FromMinutes(10));
            if (swept > 0)
                Log.Debug("[medius] ended {Count} abandoned session(s)", swept);
            (int seats, int games) = _context.Lobby.SweepGames(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5));
            if (seats + games > 0)
                Log.Debug("[medius] freed {Seats} untaken game seat(s), ended {Games} game(s)", seats, games);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[medius] tick failed");
        }
    }
}
