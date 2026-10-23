using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Plugin.Medius.Lobby;

/// <summary>
/// A game world: what the MLS lists and the DME hosts. A player creates it
/// on the MLS, then every player (the creator too) joins it there, which
/// reserves them a slot and a DME access key, and connects to the DME with
/// that key. The game's id is both its MLS world id and its DME world id.
/// </summary>
/// <remarks>
/// A game is listed once its host reports it staging or active
/// (MediusWorldReport); until then it is pending creation. It ends when its
/// last player leaves the DME, or is closed by MediusEndGameReport and
/// dropped once empty.
/// </remarks>
public sealed class MediusGame
{
    /// <summary>The most players one world seats; the DME's APP_LIST bitmask and client indices stay small.</summary>
    public const int MaxSlots = 64;

    private readonly DmePlayer?[] _slots;

    internal MediusGame(int id, MediusTitle title, MediusClient host, MediusCreateGameRequest request)
        : this(id, title, host, request.ApplicationID, request.MaxPlayers)
    {
        Name = request.GameName ?? "";
        Password = request.GamePassword ?? "";
        SpectatorPassword = request.SpectatorPassword ?? "";
        MinPlayers = request.MinPlayers;
        GameLevel = request.GameLevel;
        PlayerSkillLevel = request.PlayerSkillLevel;
        RulesSet = request.RulesSet;
        GenericFields = [request.GenericField1, request.GenericField2, request.GenericField3, request.GenericField4,
            request.GenericField5, request.GenericField6, request.GenericField7, request.GenericField8];
        HostType = request.GameHostType;
        Attributes = request.Attributes;
    }

    internal MediusGame(int id, MediusTitle title, MediusClient host, MediusCreateGameRequest1 request)
        : this(id, title, host, request.ApplicationID, request.MaxPlayers)
    {
        Name = request.GameName ?? "";
        Password = request.GamePassword ?? "";
        SpectatorPassword = request.SpectatorPassword ?? "";
        MinPlayers = request.MinPlayers;
        GameLevel = request.GameLevel;
        PlayerSkillLevel = request.PlayerSkillLevel;
        RulesSet = request.RulesSet;
        GenericFields = [request.GenericField1, request.GenericField2, request.GenericField3, 0, 0, 0, 0, 0];
        HostType = request.GameHostType;
        Attributes = request.Attributes;
    }

    private MediusGame(int id, MediusTitle title, MediusClient host, int appId, int maxPlayers)
    {
        Id = id;
        Title = title;
        Host = host;
        AppId = appId == 0 ? host.AppId : appId;
        MaxPlayers = Math.Clamp(maxPlayers, 1, MaxSlots);
        _slots = new DmePlayer?[MaxPlayers];
        Channel = host.Channel;
    }

    public int Id { get; }
    public MediusTitle Title { get; }
    public int AppId { get; }

    /// <summary>The player who created it; null once they leave.</summary>
    public MediusClient? Host { get; private set; }
    /// <summary>The lobby channel it was created from.</summary>
    public MediusChannel? Channel { get; }

    public string Name { get; private set; } = "";
    public string Password { get; } = "";
    public string SpectatorPassword { get; } = "";
    public int MinPlayers { get; private set; }
    public int MaxPlayers { get; private set; }
    public int GameLevel { get; private set; }
    public int PlayerSkillLevel { get; private set; }
    public int RulesSet { get; private set; }
    /// <summary>GenericField1..8: whatever the game keeps there (map, mode, ...); list filters compare them.</summary>
    public int[] GenericFields { get; private set; } = new int[8];
    public byte[] Stats { get; private set; } = new byte[Constants.GAMESTATS_MAXLEN];
    public MediusGameHostType HostType { get; }
    public MediusWorldAttributesType Attributes { get; }
    public MediusWorldStatus Status { get; private set; } = MediusWorldStatus.WorldPendingCreation;

    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    /// <summary>The DME's world clock (startup info, timebase queries).</summary>
    public Stopwatch Clock { get; } = Stopwatch.StartNew();
    /// <summary>Set once anyone has completed the DME connect; an empty world after that is over.</summary>
    public bool HasStarted { get; private set; }

    public bool HasPassword => Password.Length > 0;

    /// <summary>Shown in game lists: the host has reported it open.</summary>
    public bool IsListed => Status is MediusWorldStatus.WorldStaging or MediusWorldStatus.WorldActive;

    public DmePlayer[] Players
    {
        get { lock (_slots) return _slots.OfType<DmePlayer>().ToArray(); }
    }

    /// <summary>Players connected to the DME.</summary>
    public DmePlayer[] JoinedPlayers => Players.Where(p => p.Joined).ToArray();

    public int PlayerCount => JoinedPlayers.Length;

    public DmePlayer? Player(int index)
    {
        lock (_slots)
            return index >= 0 && index < _slots.Length ? _slots[index] : null;
    }

    /// <summary>
    /// Seats the client, or returns the seat it already has. Null when every
    /// seat is taken.
    /// </summary>
    internal DmePlayer? Reserve(MediusClient client)
    {
        lock (_slots)
        {
            if (_slots.FirstOrDefault(p => p?.Client == client) is { } existing)
                return existing;
            int index = Array.IndexOf(_slots, null);
            if (index < 0)
                return null;
            return _slots[index] = new DmePlayer(client, this, index);
        }
    }

    /// <summary>Frees the player's seat. Returns false when it was not seated here.</summary>
    internal bool Remove(DmePlayer player)
    {
        lock (_slots)
        {
            if (player.Index >= _slots.Length || _slots[player.Index] != player)
                return false;
            _slots[player.Index] = null;
        }
        if (Host == player.Client)
            Host = null;
        return true;
    }

    internal void MarkStarted() => HasStarted = true;

    /// <summary>
    /// The host's (or any member's) MediusWorldReport. Once closed the world
    /// stays closed: when the host chooses "play again" it reports the world
    /// closed while the others still report it active, and reopening it would
    /// leave the host's new game clashing with the old name.
    /// </summary>
    internal void Report(MediusWorldReport report)
    {
        Name = report.GameName ?? Name;
        MinPlayers = report.MinPlayers;
        MaxPlayers = Math.Clamp(report.MaxPlayers, 1, _slots.Length);
        GameLevel = report.GameLevel;
        PlayerSkillLevel = report.PlayerSkillLevel;
        RulesSet = report.RulesSet;
        GenericFields = [report.GenericField1, report.GenericField2, report.GenericField3, report.GenericField4,
            report.GenericField5, report.GenericField6, report.GenericField7, report.GenericField8];
        Stats = report.GameStats ?? Stats;
        if (Status != MediusWorldStatus.WorldClosed)
            Status = report.WorldStatus;
    }

    internal void Report(MediusWorldReport0 report)
    {
        Name = report.GameName ?? Name;
        MinPlayers = report.MinPlayers;
        MaxPlayers = Math.Clamp(report.MaxPlayers, 1, _slots.Length);
        GameLevel = report.GameLevel;
        PlayerSkillLevel = report.PlayerSkillLevel;
        RulesSet = report.RulesSet;
        GenericFields = [report.GenericField1, report.GenericField2, report.GenericField3, GenericFields[3],
            GenericFields[4], GenericFields[5], GenericFields[6], GenericFields[7]];
        Stats = report.GameStats ?? Stats;
        if (Status != MediusWorldStatus.WorldClosed)
            Status = report.WorldStatus;
    }

    internal void Close() => Status = MediusWorldStatus.WorldClosed;

    public override string ToString() => $"{Name}#{Id}";
}

/// <summary>
/// A player's seat in a game world: reserved on the MLS when they join the
/// game, taken up when they connect to the DME with its access key.
/// </summary>
public sealed class DmePlayer
{
    private static int _nextScertId = RandomNumberGenerator.GetInt32(1, 0x4000);

    internal DmePlayer(MediusClient client, MediusGame game, int index)
    {
        Client = client;
        Game = game;
        Index = index;
        AccessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(8))[..16];
        ScertId = (ushort)(Interlocked.Increment(ref _nextScertId) & 0x7FFF);
    }

    public MediusClient Client { get; }
    public MediusGame Game { get; }

    /// <summary>The player's index in the world ("DME client index"): what APP_SINGLE and APP_LIST address.</summary>
    public int Index { get; }

    /// <summary>Handed out in the MLS join response; the client presents it to the DME.</summary>
    public string AccessKey { get; }

    /// <summary>Identifies the player's RT connection; the client repeats it in its aux UDP connect.</summary>
    public ushort ScertId { get; }

    public DateTime ReservedUtc { get; } = DateTime.UtcNow;

    /// <summary>The player's DME TCP connection, once made.</summary>
    public ScertConnection? Connection { get; internal set; }

    /// <summary>The player's aux UDP endpoint, once it has connected one.</summary>
    public ScertDatagramPeer? Udp { get; internal set; }

    /// <summary>Whether the client asked for aux UDP (RT_MSG_CLIENT_CONNECT_TCP_AUX_UDP) or plain TCP.</summary>
    public bool AuxUdp { get; internal set; }

    /// <summary>Which relayed messages the client wants (SET_RECV_FLAG); PS2 clients at 108 get everything.</summary>
    public RT_RECV_FLAG RecvFlag { get; internal set; } = RT_RECV_FLAG.RECV_SINGLE | RT_RECV_FLAG.RECV_LIST;

    /// <summary>Completed the DME connect: counted, relayed to, announced to the others.</summary>
    public bool Joined { get; internal set; }

    /// <summary>The account stats blob the player last reported for this game (MediusPlayerReport).</summary>
    public byte[] Stats { get; internal set; } = new byte[Constants.ACCOUNTSTATS_MAXLEN];

    public bool Receives(RT_RECV_FLAG flag) => Connection is { MediusVersion: <= 108 } || RecvFlag.HasFlag(flag);

    /// <summary>Sends over aux UDP when asked and connected, otherwise over TCP.</summary>
    public void Send(BaseScertMessage message, bool udp = false)
    {
        if (udp && Udp is { } peer)
            peer.Send(message);
        else
            Connection?.Send(message);
    }

    public override string ToString() => $"{Client} [{Game.Id}:{Index}]";
}
