using System.Reflection;
using System.Text.Json;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// One MAG build's message type ids by name, as
/// <c>Data/mag-message-types.json</c> lists them. That file is generated from
/// each build's mage_g.elf and EBOOT.elf by Plugin.MagGameServer's
/// <c>tools/re/msgtypes.py</c>: every zNetwork message class with the type
/// its constructor passes to the NetMessage base (RTTI names, e.g.
/// <c>NetMessageCharacterListRequest</c>), plus the transport ("kernel")
/// messages the client builds by hand (<c>ClientClaimReservation</c>, ...).
/// Patches renumbered most of them (2.12 inserted 20 game messages below the
/// character messages and two account messages below MAPSHello), so every id
/// a server sends or matches comes from the table of the client's build.
/// </summary>
public sealed class MagMessageTable
{
    public const string ResourceName = "Plugin.Medius.Extensions.Mag.Data.mag-message-types.json";

    public uint Protocol { get; }
    public string Build { get; }
    private readonly Dictionary<string, ushort> _byName;
    private readonly Dictionary<ushort, string> _byType;

    private MagMessageTable(uint protocol, string build, Dictionary<string, ushort> byName, Dictionary<ushort, string> byType)
    {
        Protocol = protocol;
        Build = build;
        _byName = byName;
        _byType = byType;
    }

    /// <summary>The id of the class (or kernel message) <paramref name="name"/>, or null if this build has none.</summary>
    public ushort? IdOf(string name) => _byName.TryGetValue(name, out ushort id) ? id : null;

    /// <summary>The class a type id belongs to (the base class where a MAG subclass shares it), for logs.</summary>
    public string? NameOf(ushort type) => _byType.GetValueOrDefault(type);

    public IReadOnlyDictionary<string, ushort> All => _byName;

    private static readonly Lazy<IReadOnlyList<MagMessageTable>> Embedded = new(LoadEmbedded);

    /// <summary>Every build in the embedded table.</summary>
    public static IReadOnlyList<MagMessageTable> Builds => Embedded.Value;

    public static MagMessageTable? ForProtocol(uint protocol) => Builds.FirstOrDefault(b => b.Protocol == protocol);

    private static IReadOnlyList<MagMessageTable> LoadEmbedded()
    {
        using Stream stream = typeof(MagMessageTable).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"{ResourceName} is not embedded");
        using JsonDocument document = JsonDocument.Parse(stream);
        List<MagMessageTable> builds = [];
        foreach (JsonElement version in document.RootElement.GetProperty("versions").EnumerateArray())
        {
            Dictionary<string, ushort> byName = new(StringComparer.Ordinal);
            Dictionary<ushort, string> byType = [];
            foreach (JsonProperty kernel in version.GetProperty("kernel").EnumerateObject())
            {
                byName[kernel.Name] = kernel.Value.GetUInt16();
                byType.TryAdd(kernel.Value.GetUInt16(), kernel.Name);
            }
            // Base classes first, so a type shared with a MAG subclass ("via") is named by its base.
            foreach (JsonProperty cls in version.GetProperty("classes").EnumerateObject().OrderBy(c => c.Value.TryGetProperty("via", out _)))
            {
                ushort type = cls.Value.GetProperty("type").GetUInt16();
                byName[cls.Name] = type;
                byType.TryAdd(type, cls.Name);
            }
            builds.Add(new MagMessageTable(version.GetProperty("protocol").GetUInt32(), version.GetProperty("build").GetString() ?? "", byName, byType));
        }
        return builds;
    }
}

/// <summary>Names a message by its class (or kernel name) in <see cref="MagMessageTable"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class MagMessageAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// The ids both MAG server plugins use, for one client build. Each property
/// is resolved from <see cref="MagMessageTable"/> by the class named in its
/// attribute; a <c>ushort?</c> is a message that only some builds have. The
/// layouts (and the addresses they were read from) are on the writers in
/// <see cref="MapsWriters"/> and in Plugin.MagGameServer's docs/PROTOCOL.md.
/// </summary>
public sealed class MagMessageIds
{
    // Transport, MAPS and MAS (MapsComponent).
    [MagMessage("NetMessageHello")] public ushort Hello { get; private set; }
    [MagMessage("NetMessageProtocolInfo")] public ushort ProtocolInfo { get; private set; }
    /// <summary>Kernel keep-alive, no body; the client sends one every ~15 s and drops the connection when none comes back.</summary>
    [MagMessage("NetMessageKeepAlive")] public ushort KeepAlive { get; private set; }
    [MagMessage("NetAccountLoginRequest")] public ushort AccountLoginRequest { get; private set; }
    [MagMessage("NetAccountLoginResponse")] public ushort AccountLoginResponse { get; private set; }
    [MagMessage("NetAccountLogoutRequest")] public ushort AccountLogoutRequest { get; private set; }
    [MagMessage("NetAccountLogoutResponse")] public ushort AccountLogoutResponse { get; private set; }
    [MagMessage("NetNPAuthenticateRequest")] public ushort NpAuthenticateRequest { get; private set; }
    [MagMessage("NetNPAuthenticateResponse")] public ushort NpAuthenticateResponse { get; private set; }
    [MagMessage("NetMAPSHelloMessage")] public ushort MapsHello { get; private set; }
    [MagMessage("NetUniverseListRequest")] public ushort UniverseListRequest { get; private set; }
    [MagMessage("NetUniverseListResponse")] public ushort UniverseListResponse { get; private set; }

    // The launcher's patch check (EBOOT.BIN): game messages, no envelope.
    [MagMessage("NetMessagePatchRequest")] public ushort PatchRequest { get; private set; }
    /// <summary>1.00: the files and the DLS settings in one message.</summary>
    [MagMessage("NetMessagePatchResponse")] public ushort? PatchResponse { get; private set; }
    /// <summary>2.x: the DLS settings alone (PatchResponse without its file list).</summary>
    [MagMessage("NetMessagePatchSettings")] public ushort? PatchSettings { get; private set; }
    /// <summary>2.x: one file per message, the last one flagged.</summary>
    [MagMessage("NetMessagePatchURL")] public ushort? PatchUrl { get; private set; }

    // Characters and progression (game messages, MAPS envelope where noted in MapsWriters).
    [MagMessage("NetMessageCharacterListRequest")] public ushort CharacterListRequest { get; private set; }
    [MagMessage("NetMessageCharacterListResponse")] public ushort CharacterListResponse { get; private set; }
    [MagMessage("NetMessageCharacterDataRequest")] public ushort CharacterDataRequest { get; private set; }
    [MagMessage("NetMessageCharacterDataResponse")] public ushort CharacterDataResponse { get; private set; }
    /// <summary>The soldier's record alone (1.00 0x24F, 2.12 0x267; demarshal 2.12 0x7125cc: the record reader 0x70e914 and nothing else).</summary>
    [MagMessage("NetMessageCharacterRecord")] public ushort CharacterRecord { get; private set; }
    [MagMessage("NetMessageCreateCharacterRequest")] public ushort CreateCharacterRequest { get; private set; }
    [MagMessage("NetMessageCreateCharacterResponse")] public ushort CreateCharacterResponse { get; private set; }
    [MagMessage("NetMessageDeleteCharacterRequest")] public ushort DeleteCharacterRequest { get; private set; }
    [MagMessage("NetMessageDeleteCharacterResponse")] public ushort DeleteCharacterResponse { get; private set; }
    [MagMessage("NetMessageNewsEulaRequest")] public ushort NewsEulaRequest { get; private set; }
    [MagMessage("NetMessageNewsEulaResponse")] public ushort NewsEulaResponse { get; private set; }
    [MagMessage("NetMessageServerStatusRequest")] public ushort ServerStatusRequest { get; private set; }
    [MagMessage("NetMessageServerStatusResponse")] public ushort ServerStatusResponse { get; private set; }
    [MagMessage("NetMessageLoadoutUpdateRequest")] public ushort LoadoutUpdateRequest { get; private set; }
    [MagMessage("NetMessageLoadoutUpdateResponse")] public ushort LoadoutUpdateResponse { get; private set; }
    [MagMessage("NetMessageLoadoutDeleteRequest")] public ushort LoadoutDeleteRequest { get; private set; }
    [MagMessage("NetMessageLoadoutDeleteResponse")] public ushort LoadoutDeleteResponse { get; private set; }
    [MagMessage("NetMessagePurchaseSkillRequest")] public ushort PurchaseSkillRequest { get; private set; }
    [MagMessage("NetMessagePurchaseSkillResponse")] public ushort PurchaseSkillResponse { get; private set; }
    /// <summary>Loadout index u8; no response. Sent in the lobby and in the staging lobby.</summary>
    [MagMessage("NetMessageSelectLoadout")] public ushort SelectLoadout { get; private set; }
    [MagMessage("MagNetMessageSkillReSpecRequest")] public ushort SkillReSpecRequest { get; private set; }
    [MagMessage("MagNetMessageSkillReSpecResponse")] public ushort SkillReSpecResponse { get; private set; }
    [MagMessage("MagNetMessageFactionReSpecRequest")] public ushort FactionReSpecRequest { get; private set; }
    [MagMessage("MagNetMessageVeteranReSpecRequest")] public ushort VeteranReSpecRequest { get; private set; }
    [MagMessage("MagNetMessageCharacterReSpecResponse")] public ushort CharacterReSpecResponse { get; private set; }
    /// <summary>NetTrainingCompletedRequest (envelope only, transID 0): sent on entering the lobby.</summary>
    [MagMessage("NetTrainingCompletedRequest")] public ushort TrainingCompletedRequest { get; private set; }

    // Joining a battle.
    [MagMessage("NetJoinGameRequest")] public ushort JoinGameRequest { get; private set; }
    [MagMessage("NetJoinGameResponse")] public ushort JoinGameResponse { get; private set; }
    [MagMessage("NetMessageJoinGameUpdate")] public ushort JoinGameUpdate { get; private set; }
    [MagMessage("NetCancelJoinRequest")] public ushort CancelJoinRequest { get; private set; }
    [MagMessage("NetMessageJoinGameConfirm")] public ushort JoinGameConfirm { get; private set; }

    // The game server connection (Plugin.MagGameServer): transport messages without a class.
    [MagMessage("ClientClaimReservation")] public ushort ClientClaimReservation { get; private set; }
    [MagMessage("ClientGameInfo")] public ushort ClientGameInfo { get; private set; }
    [MagMessage("ClientReady")] public ushort ClientReady { get; private set; }
    [MagMessage("ClientEnterGame")] public ushort ClientEnterGame { get; private set; }
    [MagMessage("FieldUpdate")] public ushort FieldUpdate { get; private set; }
    [MagMessage("CorrectionAck")] public ushort CorrectionAck { get; private set; }
    [MagMessage("NetTimeRequest")] public ushort NetTimeRequest { get; private set; }
    [MagMessage("NetTimeResponse")] public ushort NetTimeResponse { get; private set; }
    [MagMessage("PluginId")] public ushort PluginId { get; private set; }

    /// <summary>2.x: the claim's answer, the client index alone (type 5, demarshal 0x16aa64; handler 0x152690 completes the claim).</summary>
    [MagMessage("NetMessageClientReservationResponse")] public ushort? ClientReservationResponse { get; private set; }
    /// <summary>2.x: the game's level, clock, PMCs and points, split off 1.00's ClientGameInfo (demarshal 0x3d845c).</summary>
    [MagMessage("NetMessageGameInfo")] public ushort? GameInfo { get; private set; }

    /// <summary>2.x: when the game's clock ends (one u32, marshal 0x10b690), for the HUD's NetClockEndGameTimer (0x4a301c).</summary>
    [MagMessage("NetMessageClockEndGameTime")] public ushort? ClockEndGameTime { get; private set; }

    // The game server connection: game messages.
    [MagMessage("NetMessageHit")] public ushort Hit { get; private set; }
    [MagMessage("NetMessageDeath")] public ushort Death { get; private set; }
    [MagMessage("NetMessageCharacterStats")] public ushort CharacterStats { get; private set; }
    /// <summary>No body: the scoreboard is final (the post game's ScoreboardIsFinalized, which gates its exit).</summary>
    [MagMessage("NetMessageCharacterStatsComplete")] public ushort CharacterStatsComplete { get; private set; }
    /// <summary>A team's points changed: the new total and why (the clock's bleed, an objective, a death).</summary>
    [MagMessage("NetMessageScoreChange")] public ushort ScoreChange { get; private set; }
    /// <summary>The game is over: who won, how, and when the end-of-game countdown runs out.</summary>
    [MagMessage("NetMessageEndOfGame")] public ushort EndOfGame { get; private set; }
    [MagMessage("NetMessageHeal")] public ushort Heal { get; private set; }
    /// <summary>A repair kit's use on something (the object id, then two float vectors); 2.12 0x22F.</summary>
    [MagMessage("NetMessageRepair")] public ushort Repair { get; private set; }
    [MagMessage("NetMessageMountSeat")] public ushort MountSeat { get; private set; }
    [MagMessage("NetMessageDisMountSeat")] public ushort DisMountSeat { get; private set; }
    [MagMessage("NetMessageKillSelf")] public ushort KillSelf { get; private set; }
    [MagMessage("NetMessageResuscitate")] public ushort Resuscitate { get; private set; }
    /// <summary>
    /// An object message (ids 1000–1499 carry a u32 object id after the type,
    /// 1.00 0x148658): a player starting or stopping an objective, and the
    /// server's answer. 1002 in both builds.
    /// </summary>
    [MagMessage("NetMessageObjectiveMessage")] public ushort ObjectiveMessage { get; private set; }
    [MagMessage("NetMessageFireGrenade")] public ushort FireGrenade { get; private set; }
    [MagMessage("NetMessageConfirmRespawn")] public ushort ConfirmRespawn { get; private set; }
    [MagMessage("NetMessageCancelRespawn")] public ushort CancelRespawn { get; private set; }
    [MagMessage("NetMessageConfirmRespawnResult")] public ushort ConfirmRespawnResult { get; private set; }
    [MagMessage("NetMessageCancelRespawnResult")] public ushort CancelRespawnResult { get; private set; }
    [MagMessage("NetMessageCharacterSpawnSelection")] public ushort CharacterSpawnSelection { get; private set; }
    [MagMessage("NetMessagePingReq")] public ushort PingRequest { get; private set; }
    [MagMessage("NetMessagePingResp")] public ushort PingResponse { get; private set; }

    public MagMessageTable Table { get; }

    private MagMessageIds(MagMessageTable table)
    {
        Table = table;
        List<string> missing = [];
        foreach (PropertyInfo property in typeof(MagMessageIds).GetProperties())
        {
            if (property.GetCustomAttribute<MagMessageAttribute>() is not { } message)
                continue;
            ushort? id = table.IdOf(message.Name);
            if (id is null && property.PropertyType == typeof(ushort))
                missing.Add(message.Name);
            else
                property.SetValue(this, id);
        }
        if (missing.Count > 0)
            throw new InvalidDataException($"the protocol {table.Protocol} message table has no {string.Join(", ", missing)}");
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, MagMessageIds?> Cache = new();

    /// <summary>The ids of the build that speaks <paramref name="protocol"/>, or null if the table has no such build.</summary>
    public static MagMessageIds? ForProtocol(uint protocol) =>
        Cache.GetOrAdd(protocol, p => MagMessageTable.ForProtocol(p) is { } table ? new MagMessageIds(table) : null);

    /// <summary>The class name of <paramref name="type"/> in this build, or its hex value, for logs.</summary>
    public string Describe(ushort type) => Table.NameOf(type) is { } name ? $"0x{type:X2} {name}" : $"0x{type:X2}";
}
