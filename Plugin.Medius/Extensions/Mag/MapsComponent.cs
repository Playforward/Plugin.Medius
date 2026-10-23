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
/// Hello                    -> ProtocolInfo(1740, 0)
/// ProtocolInfo             -> MAPSHello (server key, online, chunk size)
/// NpAuthenticateRequest*   -> NpAuthenticateResponse once the last chunk is in
/// UniverseListRequest      -> UniverseListResponse per universe (MAS address)
/// AccountLogoutRequest     -> AccountLogoutResponse
/// PatchRequest (launcher)  -> PatchResponse with no files
/// </code>
/// The launcher only sends PatchRequest; an empty answer makes it boot
/// bin/mage_g.self, which reconnects and goes on to the stock MAS with the
/// universe it picked.
/// </summary>
public sealed class MapsComponent(MediusContext context, MagSettings settings) : MediusComponent(context)
{
    public const string ComponentName = "MAPS";
    private const string TicketKey = "mag.ticket";

    public override string Name => ComponentName;

    /// <summary>Characters by account id, saved to <c>&lt;DataDir&gt;/mag-characters.json</c> on every change.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, MagCharacter> _characters = LoadCharacters(CharactersPath(context));

    private static string CharactersPath(MediusContext context) => Path.Combine(context.Settings.DataDir, "mag-characters.json");

    private static System.Collections.Concurrent.ConcurrentDictionary<int, MagCharacter> LoadCharacters(string path)
    {
        try
        {
            if (File.Exists(path))
                return new(System.Text.Json.JsonSerializer.Deserialize<Dictionary<int, MagCharacter>>(File.ReadAllText(path)) ?? []);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[medius/MAPS] could not read {Path}; starting with no MAG characters", path);
        }
        return new();
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
        Log.Debug("{Tag} {Connection}: plugin {Plugin} message 0x{Type:X2} {Bytes}", Tag, connection, record.PluginId, type, Convert.ToHexString(record.Data));

        switch (type)
        {
            case MapsTypes.Hello:
                Reply(connection, MapsWriters.ProtocolInfo(settings.Protocol, settings.Build));
                break;

            case MapsTypes.KeepAlive:
                Reply(connection, [0x00, (byte)MapsTypes.KeepAlive]);
                break;

            case MapsTypes.ProtocolInfo:
                uint protocol = reader.ReadU32();
                uint build = reader.ReadU32();
                if (protocol != settings.Protocol)
                    Log.Warning("{Tag} {Connection}: client speaks protocol {Protocol} (build {Build}); this server is set to {Ours}",
                        Tag, connection, protocol, build, settings.Protocol);
                // Only MAPS greets; on the MAS the client goes straight on to NP auth.
                if (connection.Component == ComponentName)
                    Reply(connection, MapsWriters.MapsHello(ServerEnvelope(0), success: true, online: true,
                        settings.AvailableFactions, settings.HelloMessage, settings.TicketChunkSize));
                break;

            case MapsTypes.NpAuthenticateRequest:
                Authenticate(connection, title, MapsNpAuthenticateChunk.Read(reader));
                break;

            case MapsTypes.UniverseListRequest:
                SendUniverses(connection, title, MapsEnvelope.Read(reader));
                break;

            case MapsTypes.AccountLogoutRequest:
                MapsEnvelope logout = MapsEnvelope.Read(reader);
                // MAG logs out of MAPS and of the MAS each time it moves on to the
                // next server; the session (and the keys the MLS resumes it by)
                // has to survive that. Idle sessions are swept instead.
                Reply(connection, MapsWriters.StatusResponse(MapsTypes.AccountLogoutResponse, ServerEnvelope(logout.TransId), true));
                break;

            case MapsTypes.NewsEulaRequest:
                MapsEnvelope newsRequest = MapsEnvelope.Read(reader);
                string language = reader.BitsLeft >= 16 ? reader.ReadString() : "";
                Log.Information("{Tag} {Connection}: news/EULA request ({Language})", Tag, connection, language);
                Reply(connection, MapsWriters.NewsEulaResponse(ServerEnvelope(newsRequest.TransId), 0, settings.News, 0, done: true));
                Reply(connection, MapsWriters.NewsEulaResponse(ServerEnvelope(newsRequest.TransId), 1, settings.Eula, settings.EulaVersion, done: true));
                break;

            case MapsTypes.CharacterListRequest:
                MapsEnvelope listRequest = MapsEnvelope.Read(reader);
                Log.Information("{Tag} {Connection}: character list request", Tag, connection);
                Reply(connection, MapsWriters.CharacterListResponse(ServerEnvelope(listRequest.TransId), true, 1, []));
                break;

            case MapsTypes.AccountLoginRequest:
                MapsEnvelope login = MapsEnvelope.Read(reader);
                LoginToLobby(connection, title, login, record.Data[(2 + 64 + 4)..]);
                break;

            case MapsTypes.CreateCharacterRequest:
                MapsEnvelope create = MapsEnvelope.Read(reader);
                MagCharacter created = MagCharacter.ReadCreateRequest(reader);
                if (connection.Client is { Account: { } owner })
                {
                    // A re-created soldier keeps the loadouts of the one it replaces.
                    if (_characters.TryGetValue(owner.AccountId, out MagCharacter? previous))
                        created.Loadouts = previous.Loadouts;
                    _characters[owner.AccountId] = created;
                    SaveCharacters();
                }
                Log.Information("{Tag} {Connection}: created character {Name} (faction {Faction}, {A}/{B}/{C})",
                    Tag, connection, created.Name, created.Faction, created.Choice1, created.Choice2, created.Choice3);
                Reply(connection, MapsWriters.CreateCharacterResponse(ServerEnvelope(create.TransId), 0));
                break;

            case MapsTypes.CharacterDataRequest:
                MapsEnvelope dataRequest = MapsEnvelope.Read(reader);
                byte index = reader.ReadU8();
                string wanted = MagCharacter.ReadShortString(reader);
                MagCharacter? found = connection.Client is { Account: { } account } && _characters.TryGetValue(account.AccountId, out MagCharacter? c) ? c : null;
                Log.Information("{Tag} {Connection}: character data for {Name} (index {Index}): {Found}", Tag, connection, wanted, index, found is null ? "none" : found.Name);
                Reply(connection, MapsWriters.CharacterDataResponse(ServerEnvelope(dataRequest.TransId), found is not null, index, found));
                break;

            case MapsTypes.LoadoutUpdateRequest:
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
                ZipperBitWriter loadoutReply = new();
                loadoutReply.WriteU16(MapsTypes.LoadoutUpdateResponse);
                loadoutReply.WriteBool(stored);
                Reply(connection, loadoutReply.ToArray());
                break;

            case MapsTypes.ServerStatusRequest:
                MapsEnvelope status = MapsEnvelope.Read(reader);
                Reply(connection, MapsWriters.ServerStatusResponse(ServerEnvelope(status.TransId), minPlayers: 1, queuesOpen: true));
                break;

            case MapsTypes.PatchRequest:
                MapsPatchRequest patch = MapsPatchRequest.Read(reader);
                Log.Information("{Tag} {Connection}: launcher patch check (patches {Initial}..{Highest}, build {Build}, {Language}); answering with no patches",
                    Tag, connection, patch.Initial, patch.Highest, patch.Build, patch.Language);
                Reply(connection, MapsWriters.PatchResponse([]));
                break;

            default:
                Log.Warning("{Tag} {Connection}: unhandled MAPS message 0x{Type:X2} ({Bytes})", Tag, connection, type, Convert.ToHexString(record.Data));
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
        Reply(connection, MapsWriters.StatusResponse(MapsTypes.NpAuthenticateResponse, ServerEnvelope(chunk.Envelope.TransId), true));
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
            Reply(connection, MapsWriters.AccountLoginResponse(ServerEnvelope(request.TransId),
                new MagLobbyLogin(0, -1, "", 0, 0, "", "", 0, Result: MagLobbyLogin.Failed)));
            return;
        }
        Lobby.MediusChannel channel = Context.Lobby.DefaultChannel(title);
        Log.Information("{Tag} {Connection}: account login ({Body}); sending to MLS {Host}:{Port} world {World}",
            Tag, connection, Convert.ToHexString(body), Context.PublicAddress, Context.Settings.PortOf(MediusComponentNames.Mls), channel.Id);
        Reply(connection, MapsWriters.AccountLoginResponse(ServerEnvelope(request.TransId), new MagLobbyLogin(
            LobbyWorldUid: (uint)channel.Id,
            AccountId: client.AccountId,
            ServerIp: Context.PublicAddress.ToString(),
            ServerPort: (ushort)Context.Settings.PortOf(MediusComponentNames.Mls),
            WorldId: (uint)channel.Id,
            AccessKey: client.AccessToken,
            SessionKey: client.SessionKey,
            Piuid: (uint)client.AccountId)));
    }

    private void SendUniverses(ScertConnection connection, MediusTitle title, MapsEnvelope request)
    {
        List<MediusUniverse> universes = title.Universes.Count > 0
            ? title.Universes
            : [new MediusUniverse { Name = title.Descriptor.Name }];
        string host = Context.PublicAddress.ToString();
        int masPort = Context.Settings.PortOf(MediusComponentNames.Mas);
        for (int i = 0; i < universes.Count; i++)
        {
            MediusUniverse universe = universes[i];
            string authHost = string.IsNullOrEmpty(universe.Host) ? host : universe.Host;
            Reply(connection, MapsWriters.UniverseListResponse(ServerEnvelope(request.TransId), universe.Id, universe.Name,
                authHost, authHost, (uint)(universe.Port != 0 ? universe.Port : masPort), universe.SvoUrl,
                success: true, isLast: i == universes.Count - 1));
        }
    }

    private MapsEnvelope ServerEnvelope(int transId) => new()
    {
        RsaKey = MapsEnvelope.KeyWords(Context.ServerKey.N),
        TransId = transId,
    };

    private void Reply(ScertConnection connection, byte[] stream) =>
        connection.Send(RtServerPluginToApp.Single(settings.PluginId, stream));
}
