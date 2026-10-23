using System.Net;
using System.Net.Sockets;

namespace Plugin.Medius;

/// <summary>
/// Plugin-wide settings, kept in <c>Configs/MediusConfig.json</c>. Everything
/// that differs per game lives in the title's <c>medius/title.json</c>
/// instead (see <see cref="MediusTitle"/>).
/// </summary>
public sealed class MediusSettings
{
    /// <summary>
    /// The address clients are told to connect to when one component hands
    /// them on to another (MAS to MLS, MLS to DME). Empty means this
    /// machine's first LAN IPv4 address.
    /// </summary>
    public string PublicHost { get; set; } = "";

    /// <summary>
    /// Which component answers on which ServerApp listener. Each port also
    /// needs a matching entry in <c>ServerAppSettings.json</c> (TCP, or
    /// <c>UseUDP</c> for NAT and the DME's aux UDP). A title extension may
    /// add components of its own (MAG adds <c>MAPS</c>).
    /// </summary>
    public List<MediusListener> Listeners { get; set; } =
    [
        new() { Component = MediusComponentNames.Muis, Port = 10071 },
        new() { Component = MediusComponentNames.Mas, Port = 10075 },
        new() { Component = MediusComponentNames.Mls, Port = 10078 },
        new() { Component = MediusComponentNames.Nat, Port = 10070, Udp = true },
        new() { Component = MediusComponentNames.Dme, Port = 10079 },
        new() { Component = MediusComponentNames.Dme, Port = 50000, Udp = true },
    ];

    /// <summary>
    /// The server's RSA key, as decimal strings. Sent in the PS3 server
    /// hello; PS2 clients encrypt their first message to it. The default is
    /// the well-known SCE-RT development key every Medius emulator uses,
    /// which the retail PS2 libraries also accept.
    /// </summary>
    public MediusKeySettings ServerKey { get; set; } = new();

    /// <summary>Seconds between server echoes; also keeps idle connections under HttpLimits.IdleTimeoutSeconds.</summary>
    public int EchoIntervalSeconds { get; set; } = 30;

    /// <summary>Where accounts are kept, relative to the working directory.</summary>
    public string DataDir { get; set; } = "Data/medius";

    /// <summary>Log every RT message in and out at Debug level.</summary>
    public bool LogMessages { get; set; } = true;

    /// <summary>The port a component listens on, or 0 when it has no listener.</summary>
    public int PortOf(string component) =>
        Listeners.FirstOrDefault(l => string.Equals(l.Component, component, StringComparison.OrdinalIgnoreCase))?.Port ?? 0;

    /// <summary>The TCP or UDP port of a component that has both (the DME), or 0.</summary>
    public int PortOf(string component, bool udp) =>
        Listeners.FirstOrDefault(l => l.Udp == udp && string.Equals(l.Component, component, StringComparison.OrdinalIgnoreCase))?.Port ?? 0;

    public IPAddress ResolvePublicAddress()
    {
        if (!string.IsNullOrWhiteSpace(PublicHost))
        {
            if (IPAddress.TryParse(PublicHost, out IPAddress? parsed))
                return parsed;
            return Dns.GetHostAddresses(PublicHost).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        }

        using Socket probe = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Connecting a UDP socket sends nothing; it only picks the outbound interface.
            probe.Connect("10.255.255.255", 1);
            return ((IPEndPoint)probe.LocalEndPoint!).Address;
        }
        catch (SocketException)
        {
            return IPAddress.Loopback;
        }
    }
}

public sealed class MediusListener
{
    public string Component { get; set; } = "";
    public int Port { get; set; }
    public bool Udp { get; set; }
}

public sealed class MediusKeySettings
{
    public string N { get; set; } = "10315955513017997681600210131013411322695824559688299373570246338038100843097466504032586443986679280716603540690692615875074465586629501752500179100369237";
    public string E { get; set; } = "17";
    public string D { get; set; } = "4854567300243763614870687120476899445974505675147434999327174747312047455575182761195687859800492317495944895566174677168271650454805328075020357360662513";
}

/// <summary>The SCE-RT server components a listener can be assigned.</summary>
public static class MediusComponentNames
{
    /// <summary>Medius Universe Information Server: which universes exist and where their MAS is.</summary>
    public const string Muis = "MUIS";
    /// <summary>Medius Authentication Server: sessions, accounts, logins.</summary>
    public const string Mas = "MAS";
    /// <summary>Medius Lobby Server: channels, games, chat, stats.</summary>
    public const string Mls = "MLS";
    /// <summary>NAT address discovery (UDP).</summary>
    public const string Nat = "NAT";
    /// <summary>
    /// Distributed Message Engine: the game server players of one game world
    /// connect to, relaying their messages to each other. Needs a TCP and a
    /// UDP listener (the "aux UDP" channel).
    /// </summary>
    public const string Dme = "DME";
}
