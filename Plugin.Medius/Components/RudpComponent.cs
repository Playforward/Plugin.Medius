using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using ServerShared.Server;
using System.Collections.Concurrent;
using System.Net;

namespace Plugin.Medius.Components;

/// <summary>
/// The game server over SCE-RT's reliable UDP (<see cref="RudpConnection"/>):
/// where Resistance 2 goes when the lobby answers host type 5 / connection
/// type 5 [live]. Each connection names the seat it was issued in its first
/// datagram (the hello id is the hash of the seat's access key, see
/// <see cref="RudpCrypto"/>), and carries the DME's RT messages, which the
/// <see cref="DmeComponent"/> handles as it does a TCP connection's.
/// </summary>
internal sealed class RudpComponent : IDisposable
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan KeepClosed = TimeSpan.FromSeconds(15);

    private readonly MediusContext _context;
    private readonly DmeComponent _dme;
    private readonly ConcurrentDictionary<EndPoint, RudpConnection> _connections = new();
    private readonly Timer _ticker;

    public RudpComponent(MediusContext context, DmeComponent dme)
    {
        _context = context;
        _dme = dme;
        _ticker = new Timer(_ => Tick(), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    internal void HandleDatagram(CoreUdpSession session, ReadOnlySpan<byte> data)
    {
        if (session.EndPoint is not IPEndPoint remote)
            return;
        if (!RudpPacket.TryParse(data, out RudpPacket packet, out string? error))
        {
            Log.Warning("[medius/RUDP] {Remote} <- unparsed {Length}B ({Error}): {Data}", remote, data.Length, error, Convert.ToHexString(data));
            return;
        }
        if (_context.Settings.LogMessages)
            Log.Debug("[medius/RUDP] {Remote} <- {Packet}", remote, packet);

        RudpConnection? connection = _connections.GetValueOrDefault(remote);
        if (packet.Connecting && packet.Messages.FirstOrDefault(m => m.Reliable && !m.Encrypted) is { } first &&
            RudpHello.TryParse(first.Payload, out RudpHello hello) &&
            (connection is null || connection.ConnectionId != packet.ConnectionId || !connection.ClientHello.Id.AsSpan().SequenceEqual(hello.Id)))
        {
            // A new connection, possibly from the endpoint of an old one (the client
            // reuses its port for the next game [live]).
            connection?.Disconnect();
            connection = Open(session, remote, packet, hello, first.Sequence);
            if (connection is null)
                return;
        }
        if (connection is null)
        {
            Log.Debug("[medius/RUDP] {Remote}: {Packet} without a connection; dropped", remote, packet);
            return;
        }

        // One connection's messages are handled one datagram at a time, in order.
        lock (connection)
            Dispatch(connection, packet);
    }

    private void Dispatch(RudpConnection connection, RudpPacket packet)
    {
        foreach (byte[] rt in connection.Receive(packet))
        {
            if (connection.Scert is not { } scert)
                break;
            List<BaseScertMessage> messages;
            try
            {
                messages = scert.Receive(rt);
            }
            catch (Exception exception)
            {
                Log.Error(exception, "[medius/RUDP] {Connection}: RT bytes that do not parse: {Data}", connection, Convert.ToHexString(rt));
                connection.Disconnect();
                return;
            }
            foreach (BaseScertMessage message in messages)
                _dme.Handle(scert, message);
        }
    }

    private RudpConnection? Open(CoreUdpSession session, IPEndPoint remote, RudpPacket packet, RudpHello hello, ushort sequence)
    {
        DmePlayer? seat = _context.Lobby.SeatByRudpHelloId(hello.Id);
        if (seat is null)
        {
            Log.Warning("[medius/RUDP] {Remote}: hello id {Id} matches no access key handed out; the client must join a game at the MLS first",
                remote, Convert.ToHexString(hello.Id));
            return null;
        }
        RudpConnection connection = new(session.Server, remote, packet, hello, sequence, seat.AccessKey);
        connection.Scert = new ScertConnection(connection, MediusComponentNames.Rudp, _context.ServerKey, _context.Settings.LogMessages);
        connection.Closed += OnClosed;
        _connections[remote] = connection;
        Log.Information("[medius/RUDP] {Connection}: handshake for {Seat}", connection, seat);
        connection.Start();
        return connection;
    }

    private void OnClosed(RudpConnection connection, string reason)
    {
        Log.Information("[medius/RUDP] {Connection}: closed ({Reason})", connection, reason);
        if (connection.Scert is { } scert)
            _dme.Disconnected(scert);
    }

    private void Tick()
    {
        try
        {
            foreach ((EndPoint remote, RudpConnection connection) in _connections)
            {
                connection.Tick(IdleTimeout);
                if (connection.ClosedUtc is { } closed && DateTime.UtcNow - closed > KeepClosed)
                    _connections.TryRemove(new KeyValuePair<EndPoint, RudpConnection>(remote, connection));
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[medius/RUDP] tick failed");
        }
    }

    public void Dispose()
    {
        _ticker.Dispose();
        foreach (RudpConnection connection in _connections.Values)
            connection.Disconnect();
        _connections.Clear();
    }
}
