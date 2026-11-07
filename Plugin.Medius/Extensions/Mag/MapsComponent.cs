using Plugin.Medius.Components;
using Plugin.Medius.Extensions.Zipper;
using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// MAPS, Zipper's "Medius Authoritative Profile Server": the first thing MAG
/// connects to (160.33.36.126:17371 in the retail NetConfig; the launcher
/// EBOOT uses it too, to check for patches). It speaks the stock RT connect
/// sequence and then Zipper plugin messages:
/// <code>
/// Hello                    -> ProtocolInfo(MagSettings.Protocol, 0)
/// ProtocolInfo             -> MAPSHello (server key, online, chunk size)
/// NpAuthenticateRequest*   -> NpAuthenticateResponse once the last chunk is in
/// UniverseListRequest      -> UniverseListResponse per universe (MAS address)
/// AccountLogoutRequest     -> AccountLogoutResponse
/// PatchRequest (launcher)  -> PatchResponse (1.00) / PatchSettings + PatchURL (2.x), no files
/// </code>
/// The launcher only sends PatchRequest; an empty answer makes it boot
/// bin/mage_g.self, which reconnects and goes on to the stock MAS with the
/// universe it picked.
/// </summary>
public sealed class MapsComponent(MediusContext context, MagSettings settings, MagRules rules) : MediusComponent(context)
{
    public const string ComponentName = "MAPS";
    private const string TicketKey = "mag.ticket";

    public override string Name => ComponentName;

    /// <summary>Characters by account id, saved to <c>&lt;DataDir&gt;/mag-characters.json</c> on every change.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, MagCharacter> _characters = LoadCharacters(CharactersPath(context), rules);

    private static string CharactersPath(MediusContext context) => Path.Combine(context.Settings.DataDir, "mag-characters.json");

    /// <summary>The Shadow War, saved to <c>&lt;DataDir&gt;/mag-shadow-war.json</c> after every win.</summary>
    private readonly MagShadowWar _war = new(MagContractBonuses.Embedded(settings.Version), settings.ShadowWar,
        Path.Combine(context.Settings.DataDir, "mag-shadow-war.json"));

    /// <summary>
    /// Reads the saved characters; ones saved without the skill table's free
    /// skills get them now, and with <see cref="MagSettings.RaiseSavedSoldiers"/>
    /// ones below <see cref="MagSettings.StartingLevel"/> are raised to it.
    /// </summary>
    private static System.Collections.Concurrent.ConcurrentDictionary<int, MagCharacter> LoadCharacters(string path, MagRules rules)
    {
        try
        {
            if (File.Exists(path))
            {
                Dictionary<int, MagCharacter> saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, MagCharacter>>(File.ReadAllText(path)) ?? [];
                foreach (MagCharacter character in saved.Values)
                {
                    character.GrantStartingSkills(rules);
                    if (rules.Settings.RaiseSavedSoldiers && character.RaiseToLevel(rules.StartingLevel, rules))
                        Log.Information("[medius/MAPS] raised saved soldier {Name} to level {Level} ({Points} skill points)", character.Name, character.Level, character.SkillPoints);
                }
                return new(saved);
            }
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[medius/MAPS] could not read {Path}; starting with no MAG characters", path);
        }
        return new();
    }

    /// <summary>Every saved soldier.</summary>
    internal IEnumerable<MagCharacter> Characters => _characters.Values;

    /// <summary>The saved soldier of an account, if it has one.</summary>
    internal MagCharacter? CharacterOf(int accountId) => _characters.TryGetValue(accountId, out MagCharacter? character) ? character : null;

    /// <summary>
    /// Adds battle experience to the account's soldier, levels it up, saves it
    /// and sends the client the new record on <paramref name="lobby"/> (the
    /// game server calls it when a player leaves a game).
    /// </summary>
    internal void AwardExperience(int accountId, uint experience, ScertConnection? lobby)
    {
        if (experience == 0 || CharacterOf(accountId) is not { } soldier)
            return;
        int levels;
        lock (soldier)
            levels = soldier.AddExperience(experience, rules);
        Log.Information("[medius/MAPS] {Name} earned {Experience} battle experience: {Total} experience, level {Level}{Gained}",
            soldier.Name, experience, soldier.Experience, soldier.Level, levels > 0 ? $" (+{levels}, {soldier.SkillPoints} skill points)" : "");
        SaveCharacters();
        if (lobby is not null)
            Reply(lobby, MapsWriters.CharacterRecord(version, soldier));
    }

    /// <summary>
    /// The lobby's server status: every queue open, each PMC's players online
    /// (logged in with a soldier of that PMC; the same for every game type),
    /// and each game type's Shadow War: the PMCs' scores and the contracts'
    /// holders, by the client's game type numbers.
    /// </summary>
    private MagServerStatus ServerStatus(ScertConnection connection)
    {
        MagServerStatus status = MagServerStatus.Uniform(version, queuesOpen: true);
        uint[] online = new uint[MagServerStatus.Factions];
        if (connection.Title is { } title)
            foreach (MediusClient client in Context.Lobby.Clients(title))
                if (client is { IsLoggedIn: true, Account: { } account } && CharacterOf(account.AccountId) is { Faction: >= 0 and < MagServerStatus.Factions } soldier)
                    online[soldier.Faction]++;
        for (int row = 0; row < status.Rows; row++)
            for (int faction = 0; faction < MagServerStatus.Factions; faction++)
                status.PlayersOnline[row, faction] = online[faction];
        foreach (string gameType in _war.GameTypes)
        {
            if (version.GameTypes.IdOf(gameType) is not { } row)
                continue;
            for (int faction = 0; faction < MagServerStatus.Factions; faction++)
            {
                if (row < status.Rows)
                    status.WarScores[row, faction] = (uint)_war.ScoreOf(gameType, faction);
                if (row < status.Contracts.GetLength(0))
                    status.Contracts[row, faction] = (sbyte)_war.HolderOf(gameType, faction);
            }
        }
        return status;
    }

    /// <summary>A PMC's experience bonus rates in a game type: the ShadowWarBonus from the contracts it holds there.</summary>
    internal IReadOnlyDictionary<string, float> BonusRatesOf(string gameType, int faction) => new Dictionary<string, float>
    {
        ["ShadowWarBonus"] = _war.ModifierOf(gameType, faction, MagContractBonuses.ExperienceModifier),
    };

    /// <summary>A game won, from the game server: the winning PMC's Shadow War bar moves, and may take a contract.</summary>
    internal void RecordWin(string gameType, int faction, bool major)
    {
        (int Contract, int From, int To)? moved = _war.RecordWin(gameType, faction, major);
        Log.Information("[medius/MAPS] Shadow War, {GameType}: PMC {Faction} won ({Victory}); scores {Scores}, contracts held by {Holders}{Moved}",
            gameType, faction, major ? "major" : "minor",
            string.Join("/", Enumerable.Range(0, MagShadowWar.Factions).Select(f => _war.ScoreOf(gameType, f))),
            string.Join("/", Enumerable.Range(0, MagShadowWar.Factions).Select(c => _war.HolderOf(gameType, c))),
            moved is { } m ? $"; PMC {m.To} took contract {m.Contract} from PMC {m.From}" : "");
    }

    /// <summary>A player's game, from the game server: its stats go into their soldier's career, which is saved.</summary>
    internal void RecordGame(MagGameRecord record)
    {
        if (CharacterOf(record.AccountId) is not { } soldier)
            return;
        lock (soldier)
            soldier.RecordGame(record.GameType, record.Stats, MagStatTable.Embedded(version));
        Log.Information("[medius/MAPS] {Name}'s {GameType} game ({Result}) added to their career: {Stats}",
            soldier.Name, record.GameType, record.Result ?? "left early", string.Join(", ", record.Stats.Where(s => s.Value != 0).Select(s => $"{s.Key} {s.Value}")));
        SaveCharacters();
    }

    private void SaveCharacters()
    {
        string path = CharactersPath(Context);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        lock (_characters)
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new Dictionary<int, MagCharacter>(_characters), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    protected override bool HandleOther(ScertConnection connection, BaseScertMessage message)
    {
        return message is RtClientAppToPlugin plugin && HandlePlugin(connection, plugin);
    }

    /// <summary>
    /// Answers a frame of MAG plugin messages. MAG sends the same set (hello,
    /// protocol, NP auth, keep-alive...) to the MAS as to MAPS, so the
    /// extension routes the MAS's plugin traffic here too.
    /// </summary>
    internal bool HandlePlugin(ScertConnection connection, RtClientAppToPlugin plugin)
    {
        if (connection.Title is not { } title)
            return false;
        foreach (PluginRecord record in plugin.Records)
            HandleRecord(connection, title, record);
        return true;
    }

    private void HandleRecord(ScertConnection connection, MediusTitle title, PluginRecord record)
    {
        ZipperBitReader reader = new(record.Data);
        ushort type = reader.ReadU16();
        Log.Debug("{Tag} {Connection}: plugin {Plugin} message {Type} {Bytes}", Tag, connection, record.PluginId, ids.Describe(type), Convert.ToHexString(record.Data));

        // The ids are the client build's (MagMessageIds), so they can't be case labels.
        switch (type)
        {
            case var _ when type == ids.Hello:
                Reply(connection, MapsWriters.ProtocolInfo(version, settings.Protocol, settings.Build));
                break;

            case var _ when type == ids.KeepAlive:
                Reply(connection, MapsWriters.KeepAlive(version));
                break;

            case var _ when type == ids.ProtocolInfo:
                uint protocol = reader.ReadU32();
                uint build = reader.ReadU32();
                if (protocol != settings.Protocol)
                    Log.Warning("{Tag} {Connection}: client speaks protocol {Protocol} (build {Build}); this server is set to {Ours}",
                        Tag, connection, protocol, build, settings.Protocol);
                // Only MAPS greets; on the MAS the client goes straight on to NP auth.
                if (connection.Component == ComponentName)
                    Reply(connection, MapsWriters.MapsHello(version, ServerEnvelope(0), success: true, online: true,
                        settings.AvailableFactions, settings.HelloMessage, settings.TicketChunkSize));
                break;

            case var _ when type == ids.NpAuthenticateRequest:
                Authenticate(connection, title, MapsNpAuthenticateChunk.Read(reader));
                break;

            case var _ when type == ids.UniverseListRequest:
                SendUniverses(connection, title, MapsEnvelope.Read(reader));
                break;

            case var _ when type == ids.AccountLogoutRequest:
                MapsEnvelope logout = MapsEnvelope.Read(reader);
                // MAG logs out of MAPS and of the MAS each time it moves on to the
                // next server; the session (and the keys the MLS resumes it by)
                // has to survive that. Idle sessions are swept instead.
                Reply(connection, MapsWriters.StatusResponse(ids.AccountLogoutResponse, ServerEnvelope(logout.TransId), true));
                break;

            case var _ when type == ids.NewsEulaRequest:
                MapsEnvelope newsRequest = MapsEnvelope.Read(reader);
                string language = reader.BitsLeft >= 16 ? reader.ReadString() : "";
                Log.Information("{Tag} {Connection}: news/EULA request ({Language})", Tag, connection, language);
                Reply(connection, MapsWriters.NewsEulaResponse(version, ServerEnvelope(newsRequest.TransId), 0, settings.News, 0, done: true));
                Reply(connection, MapsWriters.NewsEulaResponse(version, ServerEnvelope(newsRequest.TransId), 1, settings.Eula, settings.EulaVersion, done: true));
                break;

            case var _ when type == ids.CharacterListRequest:
                MapsEnvelope listRequest = MapsEnvelope.Read(reader);
                List<MagCharacter> saved = connection.Client is { Account: { } lister } && _characters.TryGetValue(lister.AccountId, out MagCharacter? mine) ? [mine] : [];
                Log.Information("{Tag} {Connection}: character list request; {Count} saved ({Names})", Tag, connection, saved.Count, string.Join(", ", saved.Select(c => c.Name)));
                List<MagCharacterSlot> slots = saved.Select(c => new MagCharacterSlot(c)).ToList();
                while (slots.Count < Math.Max((int)settings.CharacterSlots, 1))
                    slots.Add(new MagCharacterSlot(null));
                Reply(connection, MapsWriters.CharacterListResponse(version, ServerEnvelope(listRequest.TransId), true, 1, slots));
                break;

            case var _ when type == ids.AccountLoginRequest:
                MapsEnvelope login = MapsEnvelope.Read(reader);
                LoginToLobby(connection, title, login, record.Data[(2 + 64 + 4)..]);
                break;

            case var _ when type == ids.CreateCharacterRequest:
                MapsEnvelope create = MapsEnvelope.Read(reader);
                MagCharacter created = MagCharacter.ReadCreateRequest(reader, version);
                created.SkillPoints = rules.StartingSkillPoints;
                created.RespecPoints = settings.StartingRespecPoints;
                created.RaiseToLevel(rules.StartingLevel, rules);
                created.GrantStartingSkills(rules);
                if (connection.Client is { Account: { } owner })
                {
                    // A re-created soldier keeps the loadouts of the one it replaces, if they're their PMC's.
                    if (_characters.TryGetValue(owner.AccountId, out MagCharacter? previous) && previous.Faction == created.Faction)
                        created.Loadouts = previous.Loadouts;
                    _characters[owner.AccountId] = created;
                    SaveCharacters();
                }
                Log.Information("{Tag} {Connection}: created character {Name} (faction {Faction}, head {Head}, voice {Voice}, universe {Universe}, level {Level}, {Points} skill points)",
                    Tag, connection, created.Name, created.Faction, created.HeadId, created.VoiceId, created.UniverseId, created.Level, created.SkillPoints);
                Reply(connection, MapsWriters.CreateCharacterResponse(version, ServerEnvelope(create.TransId), 0));
                break;

            case var _ when type == ids.DeleteCharacterRequest:
                MapsEnvelope deleteRequest = MapsEnvelope.Read(reader);
                MagDeleteCharacterRequest doomed = MagDeleteCharacterRequest.Read(reader);
                MagCharacter? current = CharacterOf(connection);
                string? deleteFailure = current is null ? "no character"
                    : current.Name != doomed.Name || current.Id != doomed.CharacterId ? $"it is not theirs (they have {current.Name}, id {current.Id})"
                    : null;
                if (deleteFailure is null && _characters.TryRemove(connection.Client!.AccountId, out _))
                    SaveCharacters();
                Log.Information("{Tag} {Connection}: delete character {Name} (id {Id}): {Result}", Tag, connection,
                    doomed.Name, doomed.CharacterId, deleteFailure ?? "deleted");
                Reply(connection, MapsWriters.StatusResponse(ids.DeleteCharacterResponse, ServerEnvelope(deleteRequest.TransId), deleteFailure is null));
                break;

            case var _ when type == ids.CharacterDataRequest:
                MapsEnvelope dataRequest = MapsEnvelope.Read(reader);
                byte index = reader.ReadU8();
                string wanted = MagCharacter.ReadShortString(reader);
                MagCharacter? found = connection.Client is { Account: { } account } && _characters.TryGetValue(account.AccountId, out MagCharacter? c) ? c : null;
                Log.Information("{Tag} {Connection}: character data for {Name} (index {Index}): {Found}", Tag, connection, wanted, index, found is null ? "none" : found.Name);
                if (found is not null && GameServer?.DefaultLoadouts(version, found.Faction) is { } kits && found.TakeDefaultLoadouts(kits))
                {
                    Log.Information("{Tag} {Connection}: {Name} had no loadouts: gave them PMC {Faction}'s {Count} default kits", Tag, connection, found.Name, found.Faction, kits.Count);
                    SaveCharacters();
                }
                Reply(connection, MapsWriters.CharacterDataResponse(version, ServerEnvelope(dataRequest.TransId), found is not null, index, found));
                break;

            case var _ when type == ids.LoadoutUpdateRequest:
                byte loadoutIndex = reader.ReadU8();
                MagLoadout updated = MagLoadout.Read(reader);
                bool stored = connection.Client is { Account: { } editor } && _characters.TryGetValue(editor.AccountId, out MagCharacter? soldier)
                              && loadoutIndex < soldier.Loadouts.Length;
                if (stored)
                {
                    _characters[connection.Client!.AccountId].Loadouts[loadoutIndex] = updated;
                    SaveCharacters();
                }
                Log.Information("{Tag} {Connection}: loadout {Index} updated ({Items}); {Result}", Tag, connection, loadoutIndex,
                    string.Join(",", updated.Items.Where(i => i != MagLoadout.Empty)), stored ? "stored" : "no character to store it on");
                Reply(connection, MapsWriters.GameStatusResponse(ids.LoadoutUpdateResponse, stored));
                break;

            case var _ when type == ids.LoadoutDeleteRequest:
                byte deleteIndex = reader.ReadU8();
                MagCharacter? deleter = CharacterOf(connection);
                bool deleted = deleter is not null && deleter.TryDeleteLoadout(deleteIndex);
                if (deleted)
                    SaveCharacters();
                Log.Information("{Tag} {Connection}: delete loadout {Index}: {Result}", Tag, connection, deleteIndex,
                    deleted ? "deleted" : deleter is null ? "no character" : "invalid loadout index");
                Reply(connection, MapsWriters.GameStatusResponse(ids.LoadoutDeleteResponse, deleted));
                break;

            case var _ when type == ids.SelectLoadout:
                byte selectIndex = reader.ReadU8();
                MagCharacter? selector = CharacterOf(connection);
                bool selected = selector is not null && selectIndex < selector.Loadouts.Length;
                if (selected)
                {
                    selector!.LastUsedLoadout = selectIndex;
                    SaveCharacters();
                }
                Log.Information("{Tag} {Connection}: select loadout {Index}: {Result}", Tag, connection, selectIndex,
                    selected ? "stored" : selector is null ? "no character" : "invalid loadout index");
                break;

            case var _ when type == ids.PurchaseSkillRequest:
                byte skill = reader.ReadU8();
                MagCharacter? buyer = CharacterOf(connection);
                string purchaseFailure = "no character";
                bool purchased = buyer is not null && buyer.TryPurchaseSkill(skill, rules, out purchaseFailure);
                if (purchased)
                    SaveCharacters();
                Log.Information("{Tag} {Connection}: purchase skill {Skill}: {Result}", Tag, connection, skill,
                    purchased ? $"bought, {buyer!.SkillPoints} points left" : purchaseFailure);
                Reply(connection, MapsWriters.PurchaseSkillResponse(version, purchased, buyer));
                break;

            case var _ when type == ids.SkillReSpecRequest:
                MagCharacter? respeccer = CharacterOf(connection);
                string respecFailure = "no character";
                bool respecced = respeccer is not null && respeccer.TrySkillReSpec(rules, out respecFailure);
                if (respecced)
                    SaveCharacters();
                Log.Information("{Tag} {Connection}: skill respec: {Result}", Tag, connection,
                    respecced ? $"skills refunded, {respeccer!.SkillPoints} points, respec #{respeccer.RespecCount}" : respecFailure);
                Reply(connection, MapsWriters.SkillReSpecResponse(version, respecced, respeccer));
                break;

            case var _ when type == ids.FactionReSpecRequest:
            case var _ when type == ids.VeteranReSpecRequest:
                bool veteran = type == ids.VeteranReSpecRequest;
                MagReSpecRequest change = MagReSpecRequest.Read(reader);
                MagCharacter? changer = CharacterOf(connection);
                string changeFailure = "no character";
                bool changed = changer is not null && (veteran
                    ? changer.TryVeteranReSpec(change.Faction, rules, out changeFailure)
                    : changer.TryFactionReSpec(change.Faction, rules, out changeFailure));
                if (changed)
                    SaveCharacters();
                Log.Information("{Tag} {Connection}: {Kind} respec to faction {Faction} ({Unknown}): {Result}", Tag, connection,
                    veteran ? "veteran" : "faction", change.Faction, change.Unknown,
                    changed ? $"done, veteran level {changer!.VeteranLevel}" : changeFailure);
                Reply(connection, MapsWriters.CharacterReSpecResponse(version, changed, changer));
                break;

            case var _ when type == ids.TrainingCompletedRequest:
                // MagMediusEmulation::RecvTrainingCompleted (0x919970) only logs it; no reply.
                Log.Information("{Tag} {Connection}: training completed", Tag, connection);
                break;

            case var _ when type == ids.JoinGameRequest:
                MapsEnvelope joinEnvelope = MapsEnvelope.Read(reader);
                JoinGame(connection, joinEnvelope, MagJoinGameRequest.Read(reader));
                break;

            case var _ when type == ids.CancelJoinRequest:
                CancelJoin(connection, MapsEnvelope.Read(reader));
                break;

            case var _ when type == ids.ServerStatusRequest:
                MapsEnvelope status = MapsEnvelope.Read(reader);
                Reply(connection, MapsWriters.ServerStatusResponse(version, ServerEnvelope(status.TransId), ServerStatus(connection)));
                break;

            case var _ when type == ids.PatchRequest:
                MapsPatchRequest patch = MapsPatchRequest.Read(reader, version);
                List<MapsPatchFile> files = PatchFilesFor(patch);
                Log.Information("{Tag} {Connection}: launcher patch check (patch levels [{Levels}], build {Build}, {Language}); answering with {Files}",
                    Tag, connection, string.Join(", ", patch.Levels), patch.Build, patch.Language,
                    files.Count == 0 ? "no patches" : string.Join(", ", files.Select(f => $"{f.Name} ({f.Size} bytes)")));
                foreach (byte[] reply in MapsWriters.PatchReply(version, files))
                    Reply(connection, reply);
                break;

            default:
                Log.Warning("{Tag} {Connection}: unhandled MAPS message {Type} ({Bytes})", Tag, connection, ids.Describe(type), Convert.ToHexString(record.Data));
                break;
        }
    }

    /// <summary>
    /// Collects the ticket's chunks; on the last one, takes the online ID
    /// from it and logs the player in, so the MAS that follows already knows
    /// them by the session the client brings.
    /// </summary>
    private void Authenticate(ScertConnection connection, MediusTitle title, MapsNpAuthenticateChunk chunk)
    {
        MediusClient client = connection.Client ??= Context.Lobby.BeginSession(title, connection.AppId, connection.MediusVersion);
        client.Connection ??= connection;
        List<byte> ticket = client.ExtensionState.TryGetValue(TicketKey, out object? held) ? (List<byte>)held : [];
        ticket.AddRange(chunk.Chunk);
        client.ExtensionState[TicketKey] = ticket;
        Log.Debug("{Tag} {Connection}: ticket chunk {Length}B (trailer {Trailer}, last {Last}), {Total}B so far",
            Tag, connection, chunk.Chunk.Length, chunk.Trailer, chunk.IsLast, ticket.Count);
        if (!chunk.IsLast)
            return;

        client.ExtensionState.Remove(TicketKey);
        PsnTicket? parsed = PsnTicket.TryParse(ticket.ToArray());
        string name = parsed?.OnlineId is { Length: > 0 } id ? id : $"Soldier{client.SessionKey}";
        if (parsed is null)
            Log.Warning("{Tag} {Connection}: could not read an online ID from the {Length}-byte ticket; using {Name}", Tag, connection, ticket.Count, name);

        MediusAccount account = Context.Lobby.Accounts.Find(title.Descriptor.TitleId, name)
            ?? Context.Lobby.Accounts.Create(title.Descriptor.TitleId, name, null)!;
        Context.Lobby.Accounts.Update(account, a => a.LastLoginUtc = DateTime.UtcNow);
        client.Login(account);
        Log.Information("{Tag} {Title}: {Account} authenticated by PSN ticket from {Remote}", Tag, title, client, connection.Remote);
        Reply(connection, MapsWriters.StatusResponse(ids.NpAuthenticateResponse, ServerEnvelope(chunk.Envelope.TransId), true));
    }

    /// <summary>
    /// The character logs in on the MAS: hand it the MLS address and the
    /// session's keys, which the MLS resumes the session by.
    /// </summary>
    private void LoginToLobby(ScertConnection connection, MediusTitle title, MapsEnvelope request, byte[] body)
    {
        if (connection.Client is not { IsLoggedIn: true } client)
        {
            Log.Warning("{Tag} {Connection}: account login before NP authentication", Tag, connection);
            Reply(connection, MapsWriters.AccountLoginResponse(version, ServerEnvelope(request.TransId),
                new MagLobbyLogin(0, -1, "", 0, 0, "", "", 0, Result: MagLobbyLogin.Failed)));
            return;
        }
        Lobby.MediusChannel channel = Context.Lobby.DefaultChannel(title);
        Log.Information("{Tag} {Connection}: account login ({Body}); sending to MLS {Host}:{Port} world {World}",
            Tag, connection, Convert.ToHexString(body), Context.PublicAddress, Context.Settings.PortOf(MediusComponentNames.Mls), channel.Id);
        Reply(connection, MapsWriters.AccountLoginResponse(version, ServerEnvelope(request.TransId), new MagLobbyLogin(
            LobbyWorldUid: (uint)channel.Id,
            AccountId: client.AccountId,
            ServerIp: Context.PublicAddress.ToString(),
            ServerPort: (ushort)Context.Settings.PortOf(MediusComponentNames.Mls),
            WorldId: (uint)channel.Id,
            AccessKey: client.AccessToken,
            SessionKey: client.SessionKey,
            Piuid: (uint)client.AccountId)));
    }

    /// <summary>The launcher patches the web listener serves, if any (<see cref="MagPatches"/>).</summary>
    internal MagPatches? Patches { get; init; }

    /// <summary>
    /// The PatchURL entries for the patches the launcher lacks: each one's
    /// full URL on the web listener (<see cref="MagSettings.PatchUrl"/>), the
    /// psarc kind and its size. Retail's PatchURL name was presumably such a
    /// URL [guess, from the message's name and the launcher joining a base
    /// and a file name with '/' (EBOOT 0x49a18)].
    /// </summary>
    private List<MapsPatchFile> PatchFilesFor(MapsPatchRequest request)
    {
        if (Patches is not { Files.Count: > 0 } patches || version.Format == MagWireFormat.Retail100)
            return [];
        int port = Context.Settings.PortOf(MediusComponentNames.Web);
        string baseUrl = settings.PatchUrl.Length > 0 ? settings.PatchUrl.TrimEnd('/')
            : port != 0 ? $"http://{Context.PublicAddress}:{port}{patches.UrlPath}"
            : "";
        if (baseUrl.Length == 0)
        {
            Log.Warning("{Tag}: launcher patches found but no WEB listener to serve them; offering none", Tag);
            return [];
        }
        return [.. patches.Needed(request.Levels).Select(f => new MapsPatchFile { Name = $"{baseUrl}/{f.Name}", Kind = settings.PatchKind, Size = (uint)f.Size })];
    }

    /// <summary>The battle server players are sent to, if a game server plugin registered one.</summary>
    internal IMagGameServer? GameServer { get; set; }

    /// <summary>
    /// Queues the player on the game server for the game type they asked for;
    /// the join is answered when the game server gives them a seat (sending
    /// the client there) or turns them away. The game server decides whether
    /// and when it can host them.
    /// </summary>
    private void JoinGame(ScertConnection connection, MapsEnvelope request, MagJoinGameRequest body)
    {
        int port = GameServer is { } server ? Context.Settings.PortOf(server.ComponentName) : 0;
        string? problem = connection.Client is not { IsLoggedIn: true } ? "the client is not logged in"
            : GameServer is null ? "no game server plugin is installed"
            : port == 0 ? $"no {GameServer.ComponentName} listener is configured"
            : null;
        if (problem is not null)
        {
            RefuseJoin(connection, request, body, problem);
            return;
        }
        if (body.GameTypeNames(version, settings.GameTypes) is not { } gameTypes)
        {
            RefuseJoin(connection, request, body, $"game type value {body.GameTypeId} (kind {body.Unknown03}) doesn't name game types of the protocol {settings.Protocol} client's table");
            return;
        }
        string gameType = string.Join("/", gameTypes);
        MediusClient client = connection.Client!;
        Log.Information("{Tag} {Connection}: join game request ({Body}, {GameType}); queued", Tag, connection, body, gameType);
        GameServer!.Queue(new MagJoinRequest(client.AccountId, client.AccountName, request.TransId, connection, CharacterOf(client.AccountId), gameTypes), ticket =>
        {
            if (ticket is null)
            {
                RefuseJoin(connection, request, body, $"the game server has no {gameType} game for them");
                return;
            }
            Log.Information("{Tag} {Connection}: join game request ({Body}); sending to game {Game} at {Host}:{Port}",
                Tag, connection, body, ticket.GameId, Context.PublicAddress, port);
            Reply(connection, MapsWriters.JoinGameResponse(version, ServerEnvelope(request.TransId), 0, ticket.GameId,
                connectionType: 1, addressType: 1, Context.PublicAddress.ToString(), (ushort)port, ticket.GameId, ticket.AccessKey, ticket.Piuid));
        });
    }

    private void RefuseJoin(ScertConnection connection, MapsEnvelope request, MagJoinGameRequest body, string reason)
    {
        Log.Warning("{Tag} {Connection}: join game request ({Body}) but {Reason}", Tag, connection, body, reason);
        Reply(connection, MapsWriters.JoinGameResponse(version, ServerEnvelope(request.TransId), 1, 0, 0, 0, "", 0, 0, "", 0));
    }

    /// <summary>
    /// NetCancelJoinRequest (0x15, envelope only): the client gave up
    /// waiting for its join [live: seen when the queue screen was left].
    /// What answer, if any, it expects is not known yet; none is sent.
    /// </summary>
    private void CancelJoin(ScertConnection connection, MapsEnvelope request)
    {
        bool waiting = connection.Client is { IsLoggedIn: true } client && GameServer?.Cancel(client.AccountId) == true;
        Log.Information("{Tag} {Connection}: cancelled their join (trans {TransId}){Waiting}", Tag, connection, request.TransId,
            waiting ? "; left the queue" : "; they weren't queued");
    }

    private void SendUniverses(ScertConnection connection, MediusTitle title, MapsEnvelope request)
    {
        List<MediusUniverse> universes = title.Universes.Count > 0
            ? title.Universes
            : [new MediusUniverse { Name = title.Descriptor.Name }];
        string host = Context.PublicAddress.ToString();
        int masPort = Context.Settings.PortOf(MediusComponentNames.Mas);
        int webPort = Context.Settings.PortOf(MediusComponentNames.Web);
        // the SVO (stats) on the web listener, for a universe that names none
        string svoUrl = webPort != 0 ? $"http://{host}:{webPort}{settings.SvoPath}" : "";
        for (int i = 0; i < universes.Count; i++)
        {
            MediusUniverse universe = universes[i];
            string authHost = string.IsNullOrEmpty(universe.Host) ? host : universe.Host;
            Reply(connection, MapsWriters.UniverseListResponse(version, ServerEnvelope(request.TransId), universe.Id, universe.Name,
                authHost, authHost, (uint)(universe.Port != 0 ? universe.Port : masPort), universe.SvoUrl.Length > 0 ? universe.SvoUrl : svoUrl,
                success: true, isLast: i == universes.Count - 1));
        }
    }

    /// <summary>The soldier of the account logged in on this connection, if it has one.</summary>
    private MagCharacter? CharacterOf(ScertConnection connection) =>
        connection.Client is { Account: { } account } && _characters.TryGetValue(account.AccountId, out MagCharacter? character) ? character : null;

    /// <summary>The client build this title serves (<see cref="MagSettings.Protocol"/>).</summary>
    private MagClientVersion version => settings.Version;
    private MagMessageIds ids => settings.Version.Messages;

    private MapsEnvelope ServerEnvelope(int transId) => new()
    {
        RsaKey = MapsEnvelope.KeyWords(Context.ServerKey.N),
        TransId = transId,
    };

    private void Reply(ScertConnection connection, byte[] stream) =>
        connection.Send(RtServerPluginToApp.Single(settings.PluginId, stream));
}
