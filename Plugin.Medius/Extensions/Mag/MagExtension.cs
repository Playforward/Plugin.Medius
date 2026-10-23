using Plugin.Medius.Components;
using Plugin.Medius.Extensions.Zipper;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using System.Text.Json;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// MAG (Zipper Interactive, PS3, 2010). On top of stock Medius it adds
/// MAPS, Zipper's account/profile server the client reaches before the MAS.
/// Its lobby and 256-player battles ran on Zipper's own plugin servers
/// behind the MLS and DME; those come later.
/// </summary>
public sealed class MagExtension : IMediusTitleExtension
{
    private MagSettings _settings = new();

    public string Name => "mag";

    public void Initialize(MediusTitle title, MediusContext context)
    {
        if (title.ExtensionSettings.ValueKind == JsonValueKind.Object)
            _settings = title.ExtensionSettings.Deserialize<MagSettings>(MediusTitle.JsonOptions) ?? new();
    }

    public IEnumerable<MediusComponent> CreateComponents(MediusContext context) => [_maps = new MapsComponent(context, _settings)];

    private MapsComponent? _maps;

    /// <summary>
    /// MAG talks Zipper plugin messages to the stock components too: on the
    /// MAS it repeats the MAPS sequence (hello, protocol, NP auth) and keeps
    /// the connection alive with type 3 (live, 2026-10-22). Those frames go
    /// to the MAPS handler, whose log names the component they came in on.
    /// </summary>
    public bool TryHandle(MediusComponent component, ScertConnection connection, BaseScertMessage message) =>
        component is not MapsComponent && message is RtClientAppToPlugin plugin && _maps is not null && _maps.HandlePlugin(connection, plugin);
}

/// <summary>The <c>ExtensionSettings</c> of a title using the <c>mag</c> extension.</summary>
public sealed class MagSettings
{
    /// <summary>Network protocol answered in ProtocolInfo; 1740 for the retail 1.00 client.</summary>
    public uint Protocol { get; set; } = MapsTypes.RetailProtocol;

    /// <summary>Build answered in ProtocolInfo; the retail client parses its build string to 0.</summary>
    public uint Build { get; set; } = MapsTypes.RetailBuild;

    /// <summary>The plugin id MAPS messages are sent from; the client answers to whatever it last heard.</summary>
    public byte PluginId { get; set; } = 31;

    /// <summary>Which of the three PMCs (Raven, SVER, Valor) can be picked; one bit each.</summary>
    public uint AvailableFactions { get; set; } = 0x7;

    /// <summary>The string MAPSHello carries; its use in the client is not known yet.</summary>
    public string HelloMessage { get; set; } = "";

    /// <summary>Largest ticket chunk the client may send per NpAuthenticateRequest (its buffer holds 900 bytes).</summary>
    public ushort TicketChunkSize { get; set; } = 512;

    /// <summary>News shown in the game's front end.</summary>
    public string News { get; set; } = "Welcome back to MAG.";

    /// <summary>EULA text the game may ask the player to accept.</summary>
    public string Eula { get; set; } = "This is a community server for MAG. Play fair.";

    /// <summary>Sent with the EULA; the game keeps it with the text (presumably to notice a new EULA).</summary>
    public uint EulaVersion { get; set; } = 1;
}
