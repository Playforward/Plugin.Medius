namespace Plugin.Medius.Extensions.Mag;

/// <summary>The network protocols of the MAG builds this server knows (ProtocolInfo's first field).</summary>
public static class MagProtocols
{
    /// <summary>
    /// Retail 1.00 (BCUS98110, build ABmagprod370048):
    /// NetClientImpl::CheckNetworkProtocol compares against the global at
    /// mage_g 0x15ad1d0 (launcher EBOOT 0x32b9b0) [C].
    /// </summary>
    public const uint Retail = 1740;

    /// <summary>Patch 2.12 (build ABmagprod493603): the global at mage_g 0x11ebae0, launcher EBOOT 0x2c1930 [C].</summary>
    public const uint Patch212 = 1793;

    /// <summary>
    /// The build number ProtocolInfo carries: <c>strtol</c> of the build string,
    /// which is 0 for "ABmagprod…" in every retail build.
    /// </summary>
    public const uint RetailBuild = 0;
}

/// <summary>Which wire layouts a client build uses, where builds differ (see <see cref="MagClientVersion"/>).</summary>
public enum MagWireFormat
{
    /// <summary>Retail 1.00.</summary>
    Retail100,
    /// <summary>Patch 2.x as of 2.12: Supply Depot record fields, six game types in the server status, the launcher's split patch reply.</summary>
    Patch212,
}

/// <summary>
/// Everything that depends on which MAG build the client is: its message
/// ids, its game type numbers, the progression tables it was built with, and
/// the layouts that changed between builds. A title picks one with
/// <see cref="MagSettings.Protocol"/>: the server answers the client's Hello
/// with its ProtocolInfo before the client says which protocol it speaks
/// (Hello has no body, 0xe8f108), and the client refuses a protocol other
/// than its own, so one title serves one build.
/// </summary>
public sealed class MagClientVersion
{
    public uint Protocol { get; }
    public string Name { get; }
    public MagWireFormat Format { get; }
    public MagMessageIds Messages { get; }
    public MagGameTypeTable GameTypes { get; }

    /// <summary>
    /// The folder of this build's generated tables, under each plugin's data
    /// directory (Plugin.Medius <c>Extensions/Mag/Data/&lt;folder&gt;</c>,
    /// Plugin.MagGameServer <c>Data/&lt;folder&gt;</c>).
    /// </summary>
    public string DataFolder { get; }

    private MagClientVersion(uint protocol, string name, MagWireFormat format, MagGameTypeTable gameTypes, string dataFolder)
    {
        Protocol = protocol;
        Name = name;
        Format = format;
        GameTypes = gameTypes;
        DataFolder = dataFolder;
        Messages = MagMessageIds.ForProtocol(protocol)
            ?? throw new InvalidDataException($"the embedded message table has no protocol {protocol} build");
    }

    /// <summary>Retail 1.00, protocol 1740.</summary>
    public static MagClientVersion Retail { get; } = new(MagProtocols.Retail, "1.00", MagWireFormat.Retail100, MagGameTypeTable.Retail, "v100");

    /// <summary>Patch 2.12, protocol 1793.</summary>
    public static MagClientVersion Patch212 { get; } = new(MagProtocols.Patch212, "2.12", MagWireFormat.Patch212, MagGameTypeTable.Patch212, "v212");

    public static IReadOnlyList<MagClientVersion> All { get; } = [Retail, Patch212];

    /// <summary>The build that speaks <paramref name="protocol"/>, or null if this server has no tables for it.</summary>
    public static MagClientVersion? ForProtocol(uint protocol) => All.FirstOrDefault(v => v.Protocol == protocol);

    /// <summary>The resource name of one of this build's tables embedded in Plugin.Medius.</summary>
    public string MediusResource(string file) => $"Plugin.Medius.Extensions.Mag.Data.{DataFolder}.{file}";

    public override string ToString() => $"MAG {Name} (protocol {Protocol})";
}
