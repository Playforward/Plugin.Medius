using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Models;
using System.Numerics;

namespace Plugin.Medius.Extensions.Resistance2;

/// <summary>
/// The game server's own seat (index 0) in a Resistance 2 world: the host every
/// player's network layer talks to, and the keeper of the game's replicated objects.
/// Read from PSRewired's host in five solo lobbies, a two-PC custom-match join, a
/// two-PC ranked match and one running 4-player game (MediusProxy, decrypted,
/// 2026-10-29/30) [live]:
/// <code>
/// player 00 10 key hello             -> 00 10 (player 0, zero key), 00 16 (host state, value 1)
///   (or unasked, ~200 ms after a player is ready)
/// player 00 15 step, lobby           -> 00 09 0, 00 17 1
/// player's first 00 0F info, lobby   -> 00 0F "R2 HOST"
/// player 02 B0 join announce         -> 02 B1 00
/// player 02 AD on its own object, op 1 -> snapshot of every object (02 AC 00.. 01), 02 AE its object
/// player 02 AD profile (object 0x82) -> 02 AE match 0x82 and table 0x81 to all (0x83 on the first)
/// player 02 AE create                -> kept; with 0x4000 also echoed without it to all
/// player 02 AC write                 -> kept; 02 AC FF update to all
/// player 02 AD on an object >= 0x84  -> the same to all
/// the game's first RT 0x39           -> 00 16 host state 4, 00 16 that player state 5 value 2
/// a player leaves                    -> 02 AF its object, 02 AC FF 0x82 without its session
/// </code>
/// 02 AD's first u16 is <c>object &lt;&lt; 5 | event</c>. Objects: 0x00-0x7F players
/// (their record, written by themselves), 0x81 table (three 128-byte columns per
/// player index: team, 1, 1), 0x82 match (session id per index, flags per index),
/// 0x83 settings (written by the creator: map, mode), 0x84 up made by players. Game
/// traffic itself goes player to player through the relay. Not done yet: the host's
/// match-start snapshot (02 AC 01 with 0x81 and 0x82).
/// </summary>
internal sealed class R2MatchHost(MediusGame game, Resistance2HostSettings settings)
{
    private readonly object _lock = new();
    private readonly HashSet<int> _introduced = [];
    private readonly HashSet<int> _infoAnswered = [];
    private readonly HashSet<int> _greeted = [];
    private readonly Dictionary<int, byte[]> _sessionIds = [];
    private readonly SortedDictionary<ushort, StoredObject> _objects = [];
    private byte _state = settings.LobbyState;

    public MediusGame Game { get; } = game;
    public bool Running => _state != settings.LobbyState;

    /// <summary>The player is ready (CONNECT_READY): introduce the host soon, unless the player asks first.</summary>
    public void PlayerReady(DmePlayer player)
    {
        _ = Task.Delay(settings.IntroductionDelayMs).ContinueWith(_ =>
        {
            lock (_lock)
                Introduce(player);
        }, TaskScheduler.Default);
    }

    /// <summary>A message to the host seat: <paramref name="direct"/> when sent to it alone, else a list or broadcast it is in.</summary>
    public void Received(DmePlayer from, byte[] payload, bool direct)
    {
        R2NetMessage message = R2NetMessage.Parse(payload);
        Log.Debug("[medius/resistance2] host <- {Player}{Via}: {Message}", from, direct ? "" : " (list/broadcast)", message);
        lock (_lock)
        {
            switch (message)
            {
                case R2PeerKey:
                    Introduce(from);
                    break;
                case R2PeerState state:
                    _sessionIds[from.Index] = state.SessionId;
                    break;
                case R2PeerStep when !Running:
                    Send(from, new R2PeerFlags { Flags = 0 });
                    Send(from, new R2PeerCounter { Value = 1 });
                    break;
                case R2PlayerInfo when !Running && _infoAnswered.Add(from.Index):
                    Send(from, new R2PlayerInfo { Player = 0, Data = Convert.FromHexString(settings.InfoData), Name = settings.Name });
                    break;
                case R2JoinAnnounce:
                    Send(from, new R2JoinAnnounceReply { Result = 0 });
                    break;
                case R2ObjectWrite write:
                    Store(write.Object).Write(write.Mask, write.Fields);
                    SendToAll(R2ObjectUpdate.For(write.Object, write.Mask, write.Data));
                    break;
                case R2ObjectCreate create:
                    ushort id = (ushort)(create.Object & ~R2ObjectCreate.EchoRequest);
                    StoredObject created = Store(id);
                    created.Flags = create.Flags;
                    created.Trailer = create.Trailer;
                    created.Write(create.Mask, create.Fields);
                    if ((create.Object & R2ObjectCreate.EchoRequest) != 0)
                        SendToAll(new R2ObjectCreate { Object = id, Mask = create.Mask, Flags = create.Flags, Fields = create.Fields, Trailer = create.Trailer });
                    break;
                case R2GameMessage { Type: 0xAD } own when own.EventObject == from.Index && own.EventOp == 1 && _greeted.Add(from.Index):
                    Greet(from);
                    break;
                case R2GameMessage { Type: 0xAD } profile when profile.EventObject == settings.ProfileObject && profile.EventOp == 0:
                    Profile(from, profile);
                    break;
                case R2GameMessage { Type: 0xAD } objectEvent when objectEvent.EventObject >= settings.FirstReplicatedObject:
                    SendToAll(objectEvent);
                    break;
            }
        }
    }

    /// <summary>An RT message the DME has no model for, from a player; RT 0x39 in a lobby starts the host's game.</summary>
    public void ReceivedRt(DmePlayer from, RawScertMessage message)
    {
        Log.Debug("[medius/resistance2] host <- {Player}: RT 0x{Id:X2} {Data}", from, (int)message.Id, Convert.ToHexString(message.Contents ?? []));
        if ((int)message.Id != settings.StartRtMessage)
            return;
        lock (_lock)
        {
            // Only the game's first: PSRewired sent nothing on a joiner's 0x39 [live].
            if (Running)
                return;
            _state = settings.RunningState;
            byte[] sessionId = _sessionIds.GetValueOrDefault(from.Index) ?? new byte[4];
            foreach (DmePlayer player in Game.JoinedPlayers)
            {
                Send(player, new R2PeerState { State = _state, Player = 0, Value = settings.HostStateValue });
                Send(player, new R2PeerState { State = settings.JoinedPlayerState, Player = (byte)from.Index, Value = settings.JoinedPlayerValue, SessionId = sessionId });
            }
        }
    }

    /// <summary>The player is leaving: its object goes, and its session leaves the match object [live].</summary>
    public void PlayerLeaving(DmePlayer player)
    {
        lock (_lock)
        {
            _objects.Remove((ushort)player.Index);
            _greeted.Remove(player.Index);
            _introduced.Remove(player.Index);
            _infoAnswered.Remove(player.Index);
            List<DmePlayer> others = [.. Game.JoinedPlayers.Where(p => p != player)];
            foreach (DmePlayer other in others)
                Send(other, new R2ObjectDelete { Object = (ushort)player.Index });
            if (_objects.TryGetValue(settings.MatchObject, out StoredObject? match) && match.Fields.TryGetValue(0, out byte[]? sessions) &&
                player.Index < sessions.Length / 4)
            {
                Array.Clear(sessions, player.Index * 4, 4);
                foreach (DmePlayer other in others)
                    Send(other, R2ObjectUpdate.For(settings.MatchObject, match.Mask, match.Data));
            }
        }
    }

    /// <summary>A player's first event on its own object: everything there is so far, then its object.</summary>
    private void Greet(DmePlayer player)
    {
        foreach (R2ObjectUpdate part in Snapshot())
            Send(player, part);
        StoredObject own = Store((ushort)player.Index);
        own.Flags = settings.PlayerObjectFlags;
        Send(player, own.Create((ushort)player.Index));
    }

    /// <summary>
    /// The objects with fields, packed into 02 AC messages of at most
    /// <see cref="Resistance2HostSettings.SnapshotMessageLimit"/> bytes, the last marked 01.
    /// </summary>
    private List<R2ObjectUpdate> Snapshot()
    {
        List<R2ObjectUpdate> parts = [new R2ObjectUpdate { Marker = 0 }];
        foreach ((ushort id, StoredObject obj) in _objects)
        {
            if (obj.Fields.Count == 0)
                continue;
            R2ObjectFields fields = new(id, obj.Mask, obj.Data);
            R2ObjectUpdate current = parts[^1];
            if (current.Objects.Count > 0 && 9 + current.Objects.Sum(R2ObjectUpdate.SizeOf) + R2ObjectUpdate.SizeOf(fields) > settings.SnapshotMessageLimit)
                parts.Add(current = new R2ObjectUpdate { Marker = 0 });
            current.Objects.Add(fields);
        }
        parts[^1].Marker = 1;
        return parts;
    }

    /// <summary>
    /// The player's profile: its session goes into the match object and its slot into
    /// the table, and every player gets both whole; the first profile also makes the
    /// settings object [live].
    /// </summary>
    private void Profile(DmePlayer player, R2GameMessage profile)
    {
        int slot = player.Index;
        if (slot >= settings.MatchSlots || profile.Body.Length < settings.ProfileSessionOffset + 4)
            return;
        bool first = !_objects.ContainsKey(settings.MatchObject);

        StoredObject match = MatchObject();
        byte[] sessions = match.Fields[0];
        profile.Body.AsSpan(settings.ProfileSessionOffset, 4).CopyTo(sessions.AsSpan(slot * 4));
        match.Fields[16][slot] = 1;
        match.Trailer[1] = (byte)Enumerable.Range(0, settings.MatchSlots).Count(i => sessions.AsSpan(i * 4, 4).IndexOfAnyExcept((byte)0) >= 0);

        StoredObject table = TableObject();
        byte[] teams = table.Fields[0];
        if (table.Fields[1][slot] == 0)
        {
            // Teams alternate by arrival: the creator 0, the next 1 [live, custom and ranked].
            teams[slot] = (byte)(table.Fields[1].Count(b => b != 0) % 2);
            table.Fields[1][slot] = 1;
            table.Fields[2][slot] = 1;
        }
        table.Trailer[0] = (byte)table.Fields[1].Count(b => b != 0);

        SendToAll(match.Create(settings.MatchObject));
        SendToAll(table.Create(settings.TableObject));
        if (first)
        {
            StoredObject state = Store(settings.SettingsObject);
            state.Flags = settings.PlayerObjectFlags;
            SendToAll(state.Create(settings.SettingsObject));
        }
    }

    private StoredObject MatchObject()
    {
        if (_objects.TryGetValue(settings.MatchObject, out StoredObject? match))
            return match;
        match = Store(settings.MatchObject);
        match.Fields[0] = new byte[settings.MatchSlots * 4];
        match.Fields[16] = new byte[settings.MatchSlots];
        match.Trailer = [0, 0, 0, 0];
        return match;
    }

    private StoredObject TableObject()
    {
        if (_objects.TryGetValue(settings.TableObject, out StoredObject? table))
            return table;
        table = Store(settings.TableObject);
        for (int column = 0; column < 3; column++)
            table.Fields[column] = new byte[settings.TableColumnLength];
        table.Trailer = [0, 0, 1, 1];
        return table;
    }

    private StoredObject Store(ushort id)
    {
        if (!_objects.TryGetValue(id, out StoredObject? obj))
            _objects[id] = obj = new StoredObject();
        return obj;
    }

    private void SendToAll(R2NetMessage message)
    {
        foreach (DmePlayer player in Game.JoinedPlayers)
            Send(player, message);
    }

    private void Introduce(DmePlayer player)
    {
        if (!player.Joined || !_introduced.Add(player.Index))
            return;
        Send(player, new R2PeerKey { Player = 0 });
        Send(player, new R2PeerState { State = _state, Player = 0, Value = settings.HostStateValue });
    }

    private static void Send(DmePlayer to, R2NetMessage message)
    {
        Log.Debug("[medius/resistance2] host -> {Player}: {Message}", to, message);
        to.Send(new RT_MSG_CLIENT_APP_SINGLE { TargetOrSource = 0, Payload = message.ToBytes() });
    }

    /// <summary>One replicated object: its fields by bit, merged write by write.</summary>
    private sealed class StoredObject
    {
        public SortedDictionary<int, byte[]> Fields { get; } = [];
        public byte Flags { get; set; }
        public byte[] Trailer { get; set; } = [];

        public uint Mask => Fields.Keys.Aggregate(0u, (mask, bit) => mask | (1u << bit));
        public byte[] Data => [.. Fields.Values.SelectMany(f => f)];

        public void Write(uint mask, List<byte[]> fields)
        {
            int next = 0;
            for (int bit = 0; bit < 32 && next < fields.Count; bit++)
                if ((mask & (1u << bit)) != 0)
                    Fields[bit] = fields[next++];
        }

        public R2ObjectCreate Create(ushort id) => new() { Object = id, Mask = Mask, Flags = Flags, Fields = [.. Fields.Values], Trailer = Trailer };
    }
}

/// <summary>What the host seat answers with (<see cref="R2MatchHost"/>); the defaults are PSRewired's [live].</summary>
public sealed class Resistance2HostSettings
{
    /// <summary>The name in the host's 00 0F player info.</summary>
    public string Name { get; set; } = "R2 HOST";
    /// <summary>The 18 bytes before the name in that info.</summary>
    public string InfoData { get; set; } = "400000000000000000000000000000000000";
    /// <summary>The host's 00 16 state in a lobby, and once the game runs.</summary>
    public byte LobbyState { get; set; } = 5;
    public byte RunningState { get; set; } = 4;
    /// <summary>The value in the host's own 00 16.</summary>
    public uint HostStateValue { get; set; } = 1;
    /// <summary>What the host reports for the player whose RT message started the game.</summary>
    public byte JoinedPlayerState { get; set; } = 5;
    public uint JoinedPlayerValue { get; set; } = 2;
    /// <summary>The RT message id that starts the game (R2: 0x39, payload 04 00 00 00 01).</summary>
    public int StartRtMessage { get; set; } = 0x39;
    /// <summary>02 AD events on objects from this one up go to every player; below it they are the host's.</summary>
    public int FirstReplicatedObject { get; set; } = 0x84;
    /// <summary>The object of the player's profile event (02 AD 0x1040).</summary>
    public int ProfileObject { get; set; } = 0x82;
    /// <summary>Where the session id sits in the profile's body.</summary>
    public int ProfileSessionOffset { get; set; } = 22;
    /// <summary>The host's objects: the table, the match, the settings.</summary>
    public ushort TableObject { get; set; } = 0x81;
    public ushort MatchObject { get; set; } = 0x82;
    public ushort SettingsObject { get; set; } = 0x83;
    /// <summary>Bytes of each of the table's three columns, and session slots in the match object.</summary>
    public int TableColumnLength { get; set; } = 128;
    public int MatchSlots { get; set; } = 8;
    /// <summary>The flags byte of the objects the host makes empty (players', settings).</summary>
    public byte PlayerObjectFlags { get; set; } = 0x21;
    /// <summary>Most bytes in one snapshot 02 AC message [guess: fits every split seen].</summary>
    public int SnapshotMessageLimit { get; set; } = 512;
    /// <summary>How long after a player is ready the host introduces itself unasked.</summary>
    public int IntroductionDelayMs { get; set; } = 200;
}
