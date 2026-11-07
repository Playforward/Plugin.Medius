using Plugin.Medius.Components;
using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using Plugin.Medius.Web;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Plugin.Medius.Extensions.Resistance2;

/// <summary>
/// Resistance 2 (Insomniac, PS3, 2008). Stock Medius plus one service of
/// SCEA's: the game recorder. A player creating a game does not send
/// MediusCreateGame; the lobby (lobby_gameMgr AskGameRecorderToCreateGame)
/// sends a binary message to the recorder account, which made the world and
/// answered with its id. This extension plays the recorder.
/// </summary>
/// <remarks>
/// Binary message payloads are <c>[type u8][data]</c>; the client dispatches
/// on the type (1.60 0x25ff08) and hands the handler the data. Create game is
/// type 0x0B (request, 0xE7 data bytes, built at 1.60 0x264c78) answered by
/// type 0x0C (HandleCreateGameResponse, 1.60 0x262dbc): data[4..7] the world
/// id big-endian, data[0x10] the result (0 created; 0x0B name taken, 0x02
/// protocol mismatch, others "error creating game") [C]. See the title's
/// JOURNAL.md.
///
/// In game, the game server holds index 0 of every world (the title's
/// GameServer.HostSeat) and plays the host the players' network layer talks
/// to (<see cref="R2MatchHost"/>).
/// </remarks>
public sealed class Resistance2Extension : IMediusTitleExtension, IMediusWebService
{
    private Resistance2Settings _settings = new();
    private MediusTitle _title = new();
    private MediusContext? _context;
    private readonly ConditionalWeakTable<MediusGame, R2MatchHost> _hosts = [];
    private R2StatsService? _stats;

    public string Name => "resistance2";

    public void Initialize(MediusTitle title, MediusContext context)
    {
        _title = title;
        _context = context;
        if (title.ExtensionSettings.ValueKind == JsonValueKind.Object)
            _settings = title.ExtensionSettings.Deserialize<Resistance2Settings>(MediusTitle.JsonOptions) ?? new();
        Log.Information("[medius/resistance2] game recorder account {Account}", _settings.GameRecorder.Account);
        if (_settings.Stats.Hosts.Count > 0)
            _stats = new R2StatsService(_settings.Stats, title, context.Lobby.Accounts, new AccountDataStore(context.Settings.DataDir));
    }

    /// <summary>The stats service's hosts (see <see cref="R2StatsService"/>); none when title.json sets no Stats.</summary>
    public IReadOnlyCollection<string> WebHosts => _stats?.Hosts ?? [];

    public MediusWebResponse? HandleWeb(MediusWebRequest request) => _stats?.Handle(request);

    public bool TryHandle(MediusComponent component, ScertConnection connection, BaseScertMessage message)
    {
        if (component is DmeComponent && connection.ComponentState is DmePlayer { Game.FirstPlayerIndex: > 0 } seat)
            return HandleForHost(seat, message);
        if (_context is null || component is not MlsComponent || connection.Client is not { IsLoggedIn: true } client ||
            message is not RT_MSG_CLIENT_APP_TOSERVER { Message: MediusBinaryMessage1 binary } ||
            binary.MessageType != MediusBinaryMessageType.TargetBinaryMsg || binary.Message.Length == 0)
            return false;

        MediusAccount recorder = RecorderAccount();
        if (binary.TargetAccountID != recorder.AccountId)
            return false;

        byte type = binary.Message[0];
        byte[] data = binary.Message[1..];
        if (type == _settings.GameRecorder.CreateGameRequest)
        {
            CreateGame(connection, client, binary, recorder, data);
            return true;
        }
        Log.Warning("[medius/resistance2] {Client}: game recorder message type 0x{Type:X2} not handled ({Length} bytes): {Data}",
            client, type, data.Length, Convert.ToHexString(data));
        return true;
    }

    /// <summary>
    /// What the host seat gets of a player's DME traffic: messages to index 0 (taken
    /// here), lists and broadcasts it is in (also relayed to the others as usual), the
    /// player's ready, and RT messages the DME has no model for.
    /// </summary>
    private bool HandleForHost(DmePlayer seat, BaseScertMessage message)
    {
        R2MatchHost host = _hosts.GetValue(seat.Game, game => new R2MatchHost(game, _settings.Host));
        switch (message)
        {
            case RT_MSG_CLIENT_CONNECT_READY_TCP:
                host.PlayerReady(seat);
                return false;
            case RT_MSG_CLIENT_APP_SINGLE { TargetOrSource: 0 } single:
                host.Received(seat, single.Payload ?? [], direct: true);
                return true;
            case RT_MSG_CLIENT_APP_LIST list when list.Targets.Contains(0):
                host.Received(seat, list.Payload ?? [], direct: false);
                return false;
            case RT_MSG_CLIENT_APP_BROADCAST broadcast:
                host.Received(seat, broadcast.Payload ?? [], direct: false);
                return false;
            case RawScertMessage raw:
                host.ReceivedRt(seat, raw);
                return true;
            case RT_MSG_CLIENT_DISCONNECT_WITH_REASON:
                host.PlayerLeaving(seat);
                return false;
            default:
                return false;
        }
    }

    private MediusAccount RecorderAccount() =>
        _context!.Lobby.Accounts.Find(_title.Descriptor.TitleId, _settings.GameRecorder.Account)
        ?? _context.Lobby.Accounts.Create(_title.Descriptor.TitleId, _settings.GameRecorder.Account, null)!;

    private void CreateGame(ScertConnection connection, MediusClient client, MediusBinaryMessage1 request, MediusAccount recorder, byte[] data)
    {
        Resistance2GameRecorderSettings settings = _settings.GameRecorder;
        string host = CString(data, settings.HostNameOffset, settings.HostNameLength);
        MediusMatchPool pool = new()
        {
            // Listed under the host's name, as PSRewired's recorder-made games are ("Waldo_717") [live].
            Name = host.Length > 0 ? host : client.AccountName,
            MinPlayers = 1,
            MaxPlayers = settings.MaxPlayers,
            HostType = settings.HostType,
        };
        MediusGame? game = _context!.Settings.PortOf(MediusComponentNames.Dme, udp: false) == 0
            ? null
            : _context.Lobby.CreateGame(_title, id => new MediusGame(id, _title, pool, client, pool.Name));

        byte[] reply = new byte[1 + settings.CreateGameResponseLength];
        reply[0] = settings.CreateGameResponse;
        Array.Copy(data, 0, reply, 1, Math.Min(4, data.Length));
        int worldId = game?.Id ?? -1;
        reply[1 + 4] = (byte)(worldId >> 24);
        reply[1 + 5] = (byte)(worldId >> 16);
        reply[1 + 6] = (byte)(worldId >> 8);
        reply[1 + 7] = (byte)worldId;
        reply[1 + 0x10] = game is null ? settings.CreateGameFailedResult : (byte)0;

        Log.Information("[medius/resistance2] game recorder: {Client} asked for a game ({Host}) -> {Game}",
            client, host, game?.ToString() ?? "failed (no DME listener?)");
        connection.SendApp(new MediusBinaryFwdMessage1
        {
            MessageID = request.MessageID,
            MessageType = MediusBinaryMessageType.TargetBinaryMsg,
            OriginatorAccountID = recorder.AccountId,
            Message = reply,
            OriginatorFirst = _title.RtLayout.BinaryFwdOriginatorFirst,
        });
    }

    private static string CString(byte[] data, int offset, int length)
    {
        if (offset >= data.Length)
            return "";
        int end = Array.IndexOf(data, (byte)0, offset, Math.Min(length, data.Length - offset));
        return Encoding.UTF8.GetString(data, offset, (end < 0 ? Math.Min(length, data.Length - offset) : end - offset));
    }
}

/// <summary>The <c>ExtensionSettings</c> of a title using the <c>resistance2</c> extension.</summary>
public sealed class Resistance2Settings
{
    public Resistance2GameRecorderSettings GameRecorder { get; set; } = new();
    public Resistance2HostSettings Host { get; set; } = new();
    /// <summary>The stats/XP web service (statRequest.py, leaderboards...); see R2StatsService.</summary>
    public R2StatsSettings Stats { get; set; } = new();
}

/// <summary>The game recorder service the lobby asks to create games (see <see cref="Resistance2Extension"/>).</summary>
public sealed class Resistance2GameRecorderSettings
{
    /// <summary>The account the client looks up and messages (1.60: "gameRecorder_r2_prod_master").</summary>
    public string Account { get; set; } = "gameRecorder_r2_prod_master";
    /// <summary>Binary message type of the create-game request [C 1.60 0x264c78].</summary>
    public byte CreateGameRequest { get; set; } = 0x0B;
    /// <summary>Binary message type of the answer [C 1.60 0x265b5c registers HandleCreateGameResponse for it].</summary>
    public byte CreateGameResponse { get; set; } = 0x0C;
    /// <summary>Data bytes after the type in the answer; the client reads up to data[0x10].</summary>
    public int CreateGameResponseLength { get; set; } = 0x14;
    /// <summary>Result byte sent when no world could be made ("Error Creating Game").</summary>
    public byte CreateGameFailedResult { get; set; } = 1;
    /// <summary>Where the host's name sits in the request data (after the type byte).</summary>
    public int HostNameOffset { get; set; } = 0x12;
    public int HostNameLength { get; set; } = 64;
    /// <summary>Seats in a recorder-made world.</summary>
    public int MaxPlayers { get; set; } = 64;
    public MediusGameHostType HostType { get; set; } = MediusGameHostType.MediusGameHostClientServerAuxUDP;
}
