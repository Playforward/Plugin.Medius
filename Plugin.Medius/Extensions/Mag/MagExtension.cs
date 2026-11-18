using Plugin.Medius.Components;
using Plugin.Medius.Extensions.Zipper;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using System.Text.Json;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// MAG (Zipper Interactive, PS3, 2010). On top of stock Medius it adds
/// MAPS, Zipper's account/profile server the client reaches before the MAS.
/// Its 256-player battles ran on Zipper's own game servers; ours is the
/// separate Plugin.MagGameServer plugin (see <see cref="MagGameServerHost"/>).
/// </summary>
public sealed class MagExtension : IMediusTitleExtension, Web.IMediusWebService
{
    private MagSettings _settings = new();
    private MagRules _rules = new(new(), null);

    public string Name => "mag";

    public void Initialize(MediusTitle title, MediusContext context)
    {
        if (title.ExtensionSettings.ValueKind == JsonValueKind.Object)
            _settings = title.ExtensionSettings.Deserialize<MagSettings>(MediusTitle.JsonOptions) ?? new();
        MagSkillTable? table = MagRules.LoadTable(_settings, title.Descriptor.ResourceDir);
        _rules = new MagRules(_settings, table);
        if (MagClientVersion.ForProtocol(_settings.Protocol) is null)
            Log.Warning("[medius/mag] no tables for protocol {Protocol}; using {Version}'s message ids", _settings.Protocol, _settings.Version);
        Log.Information("[medius/mag] serving {Version}", _settings.Version);
        if (table is not null)
            Log.Information("[medius/mag] skill table: {Skills} skills, max level {MaxLevel} ({Source})", table.Skills.Count, table.MaxLevel, table.Source);
        _settings.GameScriptsPath = Path.Combine(title.Descriptor.ResourceDir ?? "", _settings.GameScripts);
        string patchDir = Path.Combine(title.Descriptor.ResourceDir ?? "", "medius", _settings.PatchDir);
        _patches = new MagPatches(patchDir, _settings.PatchPath);
        Log.Information("[medius/mag] launcher patches at {Path}: {Files} from {Dir}", _patches.UrlPath,
            _patches.Files.Count == 0 ? "none" : string.Join(", ", _patches.Files.Select(f => f.Name)), patchDir);
    }

    private MagPatches? _patches;

    /// <summary>No host of its own: the launcher's patch URLs and the SVO URL name the server's address, and the web listener asks every service for those.</summary>
    public IReadOnlyCollection<string> WebHosts => [];

    /// <summary>
    /// Over "https" the 2.12 SVO client does SSL 3.0 and checks the server
    /// certificate's name against the leaf embedded in the game; present a
    /// certificate carrying that identity (see <see cref="MagSettings.SvoCertSubject"/>).
    /// Null when the SVO is plain "http" (no TLS), leaving the shared cert untouched.
    /// </summary>
    public Web.WebTlsIdentity? WebTlsIdentity =>
        string.Equals(_settings.SvoScheme, "https", StringComparison.OrdinalIgnoreCase)
            ? new Web.WebTlsIdentity(_settings.SvoCertSubject, _settings.SvoCertIssuer)
            : null;

    /// <summary>The launcher's patch files (<see cref="MagPatches"/>) and the SVO pages (<see cref="MagSvo"/>).</summary>
    public Web.MediusWebResponse? HandleWeb(Web.MediusWebRequest request)
    {
        if (_svo?.Serve(request) is { } svo)
            return svo;
        Web.MediusWebResponse? response = _patches?.Serve(request);
        if (response is not null)
            Log.Information("[medius/mag] launcher patch request {Request} from {Remote}: headers {Headers} -> {Status}",
                request, request.Remote, string.Join("; ", request.Headers.Select(h => $"{h.Key}: {h.Value}")), response.Status);
        return response;
    }

    /// <summary>
    /// MAPS, plus whatever the registered game server plugin brings (see
    /// <see cref="MagGameServerHost"/>); its components are kept out of the
    /// MAPS routing below.
    /// </summary>
    public IEnumerable<MediusComponent> CreateComponents(MediusContext context)
    {
        _maps = new MapsComponent(context, _settings, _rules) { Patches = _patches };
        (IMagGameServer? server, IEnumerable<MediusComponent> components) = MagGameServerHost.Create(new MagGameServerContext(context, _settings, _maps.CharacterOf, _maps.AwardExperience, _maps.RecordGame,
            _maps.BonusRatesOf, _maps.RecordWin));
        _maps.GameServer = server;
        _svo = new MagSvo(_settings, _maps.CharacterOf, () => _maps.Characters, id => context.Lobby.Accounts.Find(id)?.Name);
        _gameComponents = [.. components];
        if (server is null)
            Log.Information("[medius/mag] no game server plugin; joining a battle will fail");
        return [_maps, .. _gameComponents];
    }

    private MapsComponent? _maps;
    private MagSvo? _svo;
    private List<MediusComponent> _gameComponents = [];

    /// <summary>
    /// MAG talks Zipper plugin messages to the stock components too: on the
    /// MAS it repeats the MAPS sequence (hello, protocol, NP auth) and keeps
    /// the connection alive with type 3 (live, 2026-10-22). Those frames go
    /// to the MAPS handler, whose log names the component they came in on.
    /// </summary>
    public bool TryHandle(MediusComponent component, ScertConnection connection, BaseScertMessage message) =>
        component is not MapsComponent && !_gameComponents.Contains(component) && message is RtClientAppToPlugin plugin && _maps is not null && _maps.HandlePlugin(connection, plugin);
}

/// <summary>The <c>ExtensionSettings</c> of a title using the <c>mag</c> extension.</summary>
public sealed class MagSettings
{
    /// <summary>
    /// Network protocol answered in ProtocolInfo: 1740 for the retail 1.00
    /// client, 1793 for patch 2.12. It picks the client build (<see cref="Version"/>),
    /// and with it every message id, layout and table the plugins use.
    /// </summary>
    public uint Protocol { get; set; } = MagProtocols.Retail;

    /// <summary>The client build <see cref="Protocol"/> belongs to; 1.00's when the server has no tables for it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public MagClientVersion Version => MagClientVersion.ForProtocol(Protocol) ?? MagClientVersion.Retail;

    /// <summary>
    /// The client's game type numbers, by name in number order (see
    /// <see cref="MagGameTypeTable"/>); empty for the table of the client
    /// <see cref="Protocol"/> belongs to.
    /// </summary>
    public List<string> GameTypeIds { get; set; } = [];

    /// <summary>The game type numbers the client uses: <see cref="GameTypeIds"/>, else <see cref="Version"/>'s.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public MagGameTypeTable GameTypes =>
        GameTypeIds.Count > 0 ? new MagGameTypeTable(GameTypeIds) : Version.GameTypes;

    /// <summary>Build answered in ProtocolInfo; the retail client parses its build string to 0.</summary>
    public uint Build { get; set; } = MagProtocols.RetailBuild;

    /// <summary>The plugin id MAPS messages are sent from; the client answers to whatever it last heard.</summary>
    public byte PluginId { get; set; } = 31;

    /// <summary>Which of the three PMCs can be picked; one bit each (Valor, Raven, SVER are factions 0, 1, 2).</summary>
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

    /// <summary>
    /// The skill table: empty for the one embedded in the plugin for
    /// <see cref="Version"/> (extracted from that build's game data), a path relative to the title's resource
    /// directory for another, or <c>none</c> to go without it and use the
    /// flat <see cref="SkillCost"/>, <see cref="SkillReSpecCost"/> and
    /// <see cref="MaxLevel"/> instead.
    /// </summary>
    public string SkillTable { get; set; } = "";

    /// <summary>
    /// The level a new soldier starts at, like the client emulator's
    /// StartingLevel tunable (read at mage_g 0x8a2ed4; applied by its create,
    /// 0x8a4e14: experience = the table's value for that level, then
    /// AwardLevel 0x8a46bc for each level 1..StartingLevel, which sets the
    /// level and adds its skill points) [C]. 1 is retail. The 1.00 deploy
    /// screen (ui_deploy.zrb) locks Sabotage, Acquisition and Domination below
    /// CharacterLevel 3, 5 and 10 unless the soldier is a veteran; that value
    /// is the character record's m_level (getter 0xb479c reads net record
    /// +0x0c) [C], so 10 opens every mode. Clamped to the skill table's maximum.
    /// </summary>
    public byte StartingLevel { get; set; } = 1;

    /// <summary>
    /// The folder under the title's medius directory holding the launcher's
    /// DLS patches (<c>patch_r&lt;requires&gt;_&lt;provides&gt;.psarc</c>, see
    /// <see cref="MagPatches"/>). Empty or missing: no patches are offered.
    /// </summary>
    public string PatchDir { get; set; } = "patches";

    /// <summary>
    /// The game's server Lua scripts (<c>run/mag/ps3/scripts</c> from the
    /// player's own disc, holding <c>objective_control/</c>), relative to the
    /// title's resource directory or absolute. The game server runs the
    /// ones the level's script objects name when objectives change state
    /// (Sabotage's chaining, forward spawns, the clock). Missing: objectives
    /// still work, nothing chains.
    /// </summary>
    public string GameScripts { get; set; } = "scripts";

    /// <summary><see cref="GameScripts"/> resolved against the title's resource directory (set when the title loads).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string GameScriptsPath { get; set; } = "scripts";

    /// <summary>
    /// Whether the game server makes the levels' vehicles (motor pool trucks,
    /// APCs, Acquisition's transports) at their spawn points, as retail's
    /// MAGVehicle.ServerCreateVehicles (settings/gameproperties.zpr, true).
    /// Off, games run without them.
    /// </summary>
    public bool CreateVehicles { get; set; } = true;

    /// <summary>The URL path the web listener serves the patches under.</summary>
    public string PatchPath { get; set; } = "/mag/patches";

    /// <summary>
    /// The URL path of MAG's SVO service on the web listener (stats and the
    /// SVO login, <see cref="MagSvo"/>); a universe without an SvoUrl of its
    /// own is given <see cref="SvoScheme"/>://&lt;public address&gt;:&lt;WEB port&gt;,
    /// this and a closing '/'.
    /// </summary>
    public string SvoPath { get; set; } = "/mag/svo";

    /// <summary>
    /// The scheme of that SVO URL. The 2.12 client's SVO submits refuse a URL
    /// that doesn't begin with "https:" (0x84b9b4 and 0x84bd84 compare its
    /// first six characters with the string at 0x1277ba8 and fail with status
    /// 1, before any connection) [C], and a real "https" URL then does SSL 3.0
    /// and verifies the server cert against the SCERT Root Authority embedded
    /// in the game, which a community server can't satisfy. So the default is
    /// "http", which needs the "SVO: accept plain-HTTP stats URL" game patch
    /// (RE-Work/mag/patches/mag-svo-http.yml) to get past that check; without
    /// the patch the barracks career/ribbon pages stay blank either way.
    /// </summary>
    public string SvoScheme { get; set; } = "http";

    /// <summary>
    /// Subject distinguished name of the TLS certificate the 2.12 SVO client expects
    /// when <see cref="SvoScheme"/> is "https" — the leaf certificate embedded in the
    /// game (US retail "MAG 20231", read from the 2.12 <c>mage_g</c> at 0x11daae8).
    /// The client appears to match the certificate by name, not signature, so a
    /// self-signed certificate carrying this identity is accepted. Overridable per
    /// title for other regions (e.g. CN=MAG SCEE 22721). Attributes most-significant first.
    /// </summary>
    public string SvoCertSubject { get; set; } =
        "C=USA,ST=CA,L=San Diego,O=SCEA,OU=Zipper,CN=MAG 20231";

    /// <summary>
    /// Issuer distinguished name for that certificate — the SCERT Root Authority named
    /// in the game's embedded CA (0x11e2528). Paired with <see cref="SvoCertSubject"/>.
    /// </summary>
    public string SvoCertIssuer { get; set; } =
        "C=US,ST=CA,L=San Diego,O=SONY Computer Entertainment America Inc.,OU=SCERT Group,CN=SCERT Root Authority";

    /// <summary>
    /// The base URL the launcher is sent for the patches; empty for
    /// http://&lt;public address&gt;:&lt;WEB port&gt;<see cref="PatchPath"/>.
    /// </summary>
    public string PatchUrl { get; set; } = "";

    /// <summary>PatchURL's kind byte for a .psarc: 2 (0/1/2 taken to be unknown/generic/psarc; a psarc reserves three times its size on the HDD).</summary>
    public byte PatchKind { get; set; } = 2;

    /// <summary>
    /// Character slots the 2.12 client is offered, all unlocked
    /// (<see cref="MagCharacterSlot"/>). Retail gave one free slot and sold
    /// Slots A–C on the PlayStation Store (the client's "Purchase Character
    /// Slot A..C" texts). The lobby keeps one soldier per account, so only
    /// the first slot holds one; at least 1. 1.00 has no slots.
    /// </summary>
    public byte CharacterSlots { get; set; } = 1;

    /// <summary>
    /// Raise saved soldiers below <see cref="StartingLevel"/> to it when the
    /// lobby loads them, with the skill points of the levels they gain (as if
    /// awarded); soldiers at or above it are left alone.
    /// </summary>
    public bool RaiseSavedSoldiers { get; set; }

    /// <summary>
    /// Skill points a new soldier gets on top of what level 1 gives, like the
    /// emulator's AdditionalSkillPoints tunable (read at mage_g 0x8a2ef0).
    /// Points are otherwise earned by levelling up in battle, which this
    /// server cannot host yet, so the default lets a player try the skill tree.
    /// </summary>
    public byte StartingSkillPoints { get; set; } = 10;

    /// <summary>
    /// Respec points a new soldier starts with (the emulator's
    /// AdditionalRespecPoints, 0x8a2f28). The table's first skill respec
    /// needs 3000, so the default allows one.
    /// </summary>
    public uint StartingRespecPoints { get; set; } = 3000;

    /// <summary>What every skill costs when there is no skill table.</summary>
    public byte SkillCost { get; set; } = 1;

    /// <summary>Respec points every skill respec takes when there is no skill table.</summary>
    public uint SkillReSpecCost { get; set; } = 1;

    /// <summary>The level a veteran respec requires when there is no skill table (with one: its maximum, 60 in 1.00 and 70 in 2.12).</summary>
    public byte MaxLevel { get; set; } = 60;

    /// <summary>
    /// CP earned per point of battle experience, in builds with a Supply
    /// Depot (2.00 on). Retail worked it out in its lobby, which wasn't
    /// shipped. Patch 2.10's notes give "1.5 CP for every 1 XP"; armory.zpr
    /// keeps the same factor commented out (cash_xp_award_factor = 1.5). 2.11
    /// counted bonus experience too (the experience passed in already has
    /// it) and made the rate fall with level, by a curve that isn't known.
    /// </summary>
    public float CashPerExperience { get; set; } = 1.5f;

    /// <summary>How the game server forms games from its queues.</summary>
    public MagMatchmakingSettings Matchmaking { get; set; } = new();

    /// <summary>Computer-controlled players that fill out a game (Plugin.MagGameServer, with the BotBrain service).</summary>
    public MagBotSettings Bots { get; set; } = new();

    /// <summary>How won games move the Shadow War (<see cref="MagShadowWar"/>).</summary>
    public MagShadowWarSettings ShadowWar { get; set; } = new();
}

/// <summary>
/// Bots: players driven by the BotBrain service (a separate process the game
/// server connects to), which the game server seats in a game beside the
/// people in it. Off by default.
/// </summary>
public sealed class MagBotSettings
{
    public bool Enabled { get; set; }

    /// <summary>How many players, people and bots together, each team is filled up to once someone is in the game.</summary>
    public int FillPerTeam { get; set; } = 8;

    /// <summary>
    /// Fill each team to the game type's full size instead (32 a side in
    /// Suppression). Either way a bot gives its seat up as soon as a person
    /// is placed on its team, and the team is topped up again ten seconds
    /// after someone leaves.
    /// </summary>
    public bool FillToTeamSize { get; set; }

    /// <summary>
    /// Where the BotBrain service listens. Empty runs the brain inside the
    /// game server's own process (still over the same protocol, on loopback),
    /// which needs nothing else started.
    /// </summary>
    public string BrainHost { get; set; } = "";
    public int BrainPort { get; set; } = 24800;

    /// <summary>
    /// The folder of map packages (<c>&lt;level&gt;-&lt;hash&gt;.bbmap</c>, made by
    /// BotBrain's map tool from the level's collision). The game server and
    /// the brain both read them: a level with no package gets no bots.
    /// </summary>
    public string MapDirectory { get; set; } = "maps";

    /// <summary>
    /// A folder to record each game with bots into (<c>*.bbrec</c>), for
    /// BotBrain's replay viewer. Empty records nothing. Only used when the
    /// brain runs in this process; a separate brain has its own setting.
    /// </summary>
    public string RecordDirectory { get; set; } = "";

    /// <summary>
    /// Whether a bot's weapon is replicated as a player's client does (the
    /// item in hand and its ammunition count, which is what makes the others
    /// see and hear it fire). Off leaves bots holding nothing and shooting
    /// silently; a switch in case a client takes these values badly.
    /// </summary>
    public bool ShowWeapons { get; set; } = true;
}

/// <summary>
/// Matchmaking tunables. Retail MAG queued players per game type and PMC
/// until two teams could be formed (Plugin.MagGameServer's
/// docs/ORIGINAL-GAME.md); the rest comes from the game's own data
/// (gameproperties.zpr: player counts, how a map is picked).
/// </summary>
public sealed class MagMatchmakingSettings
{
    /// <summary>
    /// Players a game needs before it starts, per game type
    /// (<see cref="MagGameTypes"/> name, e.g. GameType64). A game type left
    /// out uses the game creation table's m_minPlayers (52, 102, 204 and 16
    /// for Sabotage, Acquisition, Domination and Suppression). Lower it to
    /// play with a few people, e.g. 2 for one player a side.
    /// </summary>
    public Dictionary<string, int> MinPlayers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The lowest client index a seated player gets (0 by default). A test
    /// knob: the Deck, always seated first as client 0, crashed in the decal
    /// jobs while the PC (client 1) never did (2026-11-02); 1 takes client 0
    /// out of play to tell a client-0 cause from a Deck one.
    /// </summary>
    public ushort FirstClientIndex { get; set; }

    /// <summary>The minimum for <paramref name="gameType"/>, or null to use the game's own.</summary>
    public int? MinPlayersFor(string gameType) =>
        MinPlayers.FirstOrDefault(p => string.Equals(p.Key, gameType, StringComparison.OrdinalIgnoreCase)) is { Key: not null } entry
            ? entry.Value
            : null;
}
