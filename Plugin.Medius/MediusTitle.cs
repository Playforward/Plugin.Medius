using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using ServerShared.Titles;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plugin.Medius;

/// <summary>
/// One Medius game, read from <c>&lt;ResourceDir&gt;/medius/title.json</c> of a
/// title whose <c>ProtocolFamily</c> is <c>medius</c>. A connection is matched
/// to its title by the application id in its RT connect message, so every
/// regional id the game uses is listed here.
/// </summary>
public sealed class MediusTitle
{
    public const string ProtocolFamily = "medius";
    public const string FileName = "medius/title.json";

    /// <summary>The <see cref="TitleDescriptor"/> this was loaded for; not part of the file.</summary>
    [JsonIgnore]
    public TitleDescriptor Descriptor { get; set; } = new();

    /// <summary>Every Medius application id the game connects with (one per region, usually).</summary>
    public List<int> AppIds { get; set; } = [];

    /// <summary>RC/RSA-encrypt RT messages. Off is easier to debug, but some clients insist on it.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>What the MUIS answers. Empty means one universe pointing at this server's MAS.</summary>
    public List<MediusUniverse> Universes { get; set; } = [];

    /// <summary>Lobby channels created at start. Empty means one channel named after the game.</summary>
    public List<MediusChannelDefinition> Channels { get; set; } = [];

    /// <summary>Text for MediusGetPolicy (usage and privacy policy screens).</summary>
    public string Policy { get; set; } = "";

    /// <summary>Announcements, newest first.</summary>
    public List<string> Announcements { get; set; } = [];

    /// <summary>What the lobby answers MediusVersionServerRequest with (up to 55 characters).</summary>
    public string VersionServer { get; set; } = "Plugin.Medius";

    /// <summary>News shown by the MUIS when the client asks for it.</summary>
    public string News { get; set; } = "";

    /// <summary>
    /// Log in names the server has never seen by creating their account,
    /// rather than refusing. PS3 ticket logins always create.
    /// </summary>
    public bool CreateAccountOnLogin { get; set; } = true;

    /// <summary>
    /// The title extension to load (e.g. <c>mag</c>), for games whose
    /// developer added services of their own beside stock Medius. Empty for
    /// stock games.
    /// </summary>
    public string Extension { get; set; } = "";

    /// <summary>
    /// Accounts the game expects to exist on the server rather than belong to
    /// players (e.g. Resistance 2's "gameRecorder_r2_prod_master", which game
    /// creation looks up). Created on first lookup.
    /// </summary>
    public List<string> ServiceAccounts { get; set; } = [];

    /// <summary>
    /// The SCE-RT library the client links: the ReleaseVersion its EBOOT's
    /// rt_msg_client / rt_comm print (e.g. <c>3.03.01.200809171107</c>, or just
    /// <c>3.03</c>). Picks the <see cref="ScertLibraryProfile"/> whose layouts
    /// the title gets; empty means Horizon's. See docs/RT-VERSIONS.md.
    /// </summary>
    public string RtVersion { get; set; } = "";

    /// <summary>Overrides the profile's MediusUniverseVariableInformationResponse layout.</summary>
    public MediusUniverseInfoLayout UniverseInfoLayout { get; set; } = new();

    /// <summary>What the DME sends this title's clients beyond the RT handshake.</summary>
    public MediusDmeSettings Dme { get; set; } = new();

    /// <summary>
    /// Where this title's game worlds are played when not on the DME (e.g.
    /// Resistance 2's own UDP game server); unset (Port 0) points players at the DME.
    /// </summary>
    public MediusGameServerSettings GameServer { get; set; } = new();

    /// <summary>Overrides the profile's MediusJoinGameResponse layout.</summary>
    public MediusJoinGameLayout JoinGameLayout { get; set; } = new();

    /// <summary>The library profile <see cref="RtVersion"/> names (Horizon's if it names none known).</summary>
    [JsonIgnore]
    public ScertLibraryProfile RtProfile => ScertLibraryProfile.TryFind(RtVersion, out ScertLibraryProfile profile) ? profile : ScertLibraryProfile.Default;

    /// <summary>The layouts this title's messages use: its profile's, with the title's own fields on top.</summary>
    [JsonIgnore]
    public ScertLayout RtLayout
    {
        get
        {
            ScertLayout profile = RtProfile.Layout;
            return profile with
            {
                UniverseExtendedInfoLength = UniverseInfoLayout.ExtendedInfoLength ?? profile.UniverseExtendedInfoLength,
                UniversePadAfterEndOfList = UniverseInfoLayout.PadAfterEndOfList ?? profile.UniversePadAfterEndOfList,
                JoinGameTrailingPadLength = JoinGameLayout.TrailingPadLength ?? profile.JoinGameTrailingPadLength,
                DmeServerVersionLength = Dme.ServerVersionLength ?? profile.DmeServerVersionLength,
            };
        }
    }

    /// <summary>Supersets and pools for the Medius matchmaking messages (MediusMatch*).</summary>
    public MediusMatchmakingSettings Matchmaking { get; set; } = new();

    /// <summary>Settings only the extension reads, passed through untouched.</summary>
    public JsonElement ExtensionSettings { get; set; }

    public override string ToString() => Descriptor.ToString();

    /// <summary>
    /// Loads the Medius half of every <c>medius</c> title. A title whose file
    /// is missing or unreadable is reported and skipped.
    /// </summary>
    public static List<MediusTitle> LoadAll(IEnumerable<TitleDescriptor> titles, List<string> problems)
    {
        List<MediusTitle> loaded = [];
        foreach (TitleDescriptor descriptor in titles.Where(t =>
                     string.Equals(t.ProtocolFamily, ProtocolFamily, StringComparison.OrdinalIgnoreCase)))
        {
            string path = Path.Combine(descriptor.ResourceDir, FileName);
            if (!File.Exists(path))
            {
                problems.Add($"{descriptor}: {path} not found");
                continue;
            }

            try
            {
                MediusTitle? title = JsonSerializer.Deserialize<MediusTitle>(File.ReadAllText(path), JsonOptions);
                if (title is null || title.AppIds.Count == 0)
                {
                    problems.Add($"{descriptor}: {path} lists no AppIds");
                    continue;
                }
                if (!ScertLibraryProfile.TryFind(title.RtVersion, out _))
                {
                    problems.Add($"{descriptor}: {path} names RtVersion \"{title.RtVersion}\", which no SCE-RT profile covers " +
                                 $"(known: {string.Join(", ", ScertLibraryProfile.Known.Select(p => p.Version))}); leave it empty for Horizon's layouts");
                    continue;
                }
                title.Descriptor = descriptor;
                loaded.Add(title);
            }
            catch (JsonException exception)
            {
                problems.Add($"{descriptor}: {path} is not valid: {exception.Message}");
            }
        }

        foreach (var duplicate in loaded.SelectMany(t => t.AppIds.Select(id => (id, t))).GroupBy(x => x.id).Where(g => g.Count() > 1))
            problems.Add($"app id {duplicate.Key} is claimed by {string.Join(" and ", duplicate.Select(x => x.t))}");

        return loaded;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class MediusUniverse
{
    public uint Id { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The MAS host clients are sent to; empty means this server's PublicHost.</summary>
    public string Host { get; set; } = "";
    /// <summary>The MAS port; 0 means this server's MAS listener.</summary>
    public int Port { get; set; }
    /// <summary>
    /// The SVO web service the client loads its online pages from;
    /// <see cref="PublicHostPlaceholder"/> in it becomes this server's PublicHost.
    /// </summary>
    public string SvoUrl { get; set; } = "";
    public const string PublicHostPlaceholder = "{PublicHost}";
    /// <summary>
    /// Free text the client may parse; <see cref="PublicHostPlaceholder"/> becomes this server's
    /// PublicHost. Resistance: Fall of Man reads it as "&lt;version&gt; &lt;patch URL&gt;" and fails
    /// to connect when it is empty (disc 0x1babec) [C].
    /// </summary>
    public string ExtendedInfo { get; set; } = "";
    /// <summary>The universe's Status field (INFO_STATUS); PSRewired's Fall of Man universe sends 1 [live].</summary>
    public int Status { get; set; }
    /// <summary>MaxUsers (INFO_STATUS); UserCount is the number of players signed in to the title.</summary>
    public int MaxUsers { get; set; }
}

/// <summary>
/// The universe information response's layout differs between client
/// library versions. Unset fields come from the title's
/// <see cref="ScertLibraryProfile"/>; set them only for a client that
/// differs from its library's profile.
/// </summary>
public sealed class MediusUniverseInfoLayout
{
    /// <summary>Bytes of ExtendedInfo: 128 (PS2-era) or 256 (SCE-RT 3.03 PS3, e.g. Resistance 2).</summary>
    public int? ExtendedInfoLength { get; set; }

    /// <summary>Whether three pad bytes follow EndOfList (PS2-era) or not (SCE-RT 3.03 PS3).</summary>
    public bool? PadAfterEndOfList { get; set; }
}

/// <summary>The DME's per-title behaviour after a player connects.</summary>
/// <summary>
/// A title-owned game server the lobby sends players to instead of the DME,
/// in MediusJoinGameResponse and MediusMatchFoundResponse. PSRewired's
/// Resistance 2 server answers: host type 5, connection type 5, External
/// address on UDP 11001 (address type 1 in the join reply, 4 in the match
/// reply), the NAT service second, and an all-zero server key [live R2 1.60].
/// </summary>
public sealed class MediusGameServerSettings
{
    /// <summary>The game server's port; 0 uses the DME.</summary>
    public int Port { get; set; }

    /// <summary>The game server's address; empty uses the lobby's public address.</summary>
    public string Host { get; set; } = "";

    /// <summary>ConnectInfo.Type of the join reply.</summary>
    public NetConnectionType ConnectionType { get; set; } = NetConnectionType.NetConnectionTypeClientServerTCPAuxUDP;

    /// <summary>Address type of the game server entry in MediusJoinGameResponse.</summary>
    public NetAddressType JoinAddressType { get; set; } = NetAddressType.NetAddressTypeExternal;

    /// <summary>Address type of the game server entry in MediusMatchFoundResponse.</summary>
    public NetAddressType MatchAddressType { get; set; } = NetAddressType.NetAddressTypeExternal;

    /// <summary>Send an all-zero ServerKey (no RT encryption to a game server that is not SCE-RT).</summary>
    public bool ZeroServerKey { get; set; }

    /// <summary>
    /// The game server holds player index 0 of every world itself (ScertId 1, the
    /// server's address) and players sit from index 1. PSRewired's Resistance 2 server
    /// does: a client alone in its custom match is index 1 and is told of index 0 at
    /// the server's IP, and sends it app messages [live MediusProxy 2026-10-29].
    /// </summary>
    public bool HostSeat { get; set; }

    public bool IsSet => Port != 0;
}

public sealed class MediusDmeSettings
{
    /// <summary>Whether CONNECT_COMPLETE is followed by DMEServerVersion for Medius 109+ clients (Horizon does).</summary>
    public bool SendServerVersion { get; set; } = true;

    /// <summary>
    /// Hand out an all-zero ServerKey in join replies to the DME, so the client does not encrypt its
    /// DME traffic. PSRewired's Resistance: Fall of Man game host does (join reply lobby-ext 0x68), and
    /// RFOM's encrypted QoS broadcasts are only partly encrypted, which whole-message decryption misses [live].
    /// </summary>
    public bool ZeroServerKey { get; set; }

    /// <summary>The version string DMEServerVersion carries.</summary>
    public string ServerVersion { get; set; } = "2.10.0009";

    /// <summary>
    /// Bytes of the version string on the wire; unset takes the title's
    /// <see cref="ScertLibraryProfile"/>. 16 is Horizon's; the SCE-RT 3.03 DME
    /// reads 20 (Resistance 2: reader 0x851798, handler 0x8529b0 copies 0x14
    /// bytes to conn+0x41c). Too few bytes underrun the reader (stream error
    /// 8, shown as "Network error (00000019)") [C 1.60; live].
    /// </summary>
    public int? ServerVersionLength { get; set; }
}

/// <summary>
/// MediusJoinGameResponse's tail differs between client library versions.
/// Unset takes the title's <see cref="ScertLibraryProfile"/>.
/// </summary>
public sealed class MediusJoinGameLayout
{
    /// <summary>
    /// Bytes after ConnectInfo; unset takes the profile's (Horizon's profile
    /// keeps its version rule: 4 from Medius 112 on). SCE-RT 3.03 PS3 clients
    /// (Resistance 2) read none: the struct is 0xBC bytes, ending with
    /// ConnectInfo's 2 pad bytes [C R2 1.00 swap 0x816580, handler 0x80b1c0].
    /// </summary>
    public int? TrailingPadLength { get; set; }
}

public sealed class MediusChannelDefinition
{
    public string Name { get; set; } = "";
    public int MaxPlayers { get; set; } = 256;
}
