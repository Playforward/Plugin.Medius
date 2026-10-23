using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Components;

/// <summary>
/// Game worlds on the MLS: creating, listing, joining and reporting on them.
/// Joining hands the client a seat on the DME (<see cref="DmeComponent"/>),
/// where the game itself is played.
/// </summary>
/// <remarks>
/// A host's game goes: MediusCreateGame (pending) -> MediusJoinGame (seat
/// and DME address) -> DME connect -> MediusWorldReport staging/active
/// (now listed) -> MediusEndGameReport or everyone leaving the DME (gone).
/// Ported from Horizon's MLS game handlers (MIT).
/// </remarks>
public sealed partial class MlsComponent
{
    private const int DefaultGameListPageSize = 50;

    /// <summary>Handles a game request; false for anything else.</summary>
    private bool HandleGame(ScertConnection connection, MediusClient client, BaseMediusMessage message)
    {
        MediusTitle title = client.Title;
        switch (message)
        {
            case MediusCreateGameRequest request:
                CreateGame(connection, client, request.MessageID, id => new MediusGame(id, title, client, request));
                return true;
            case MediusCreateGameRequest1 request:
                CreateGame(connection, client, request.MessageID, id => new MediusGame(id, title, client, request));
                return true;

            case MediusJoinGameRequest request:
                JoinGame(connection, client, request);
                return true;

            case MediusGameList_ExtraInfoRequest request:
                SendGameList(connection, client, request.PageID, request.PageSize, request.MessageID, (game, last) => new MediusGameList_ExtraInfoResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    MediusWorldID = game.Id,
                    PlayerCount = (ushort)game.PlayerCount,
                    MinPlayers = (ushort)game.MinPlayers,
                    MaxPlayers = (ushort)game.MaxPlayers,
                    GameLevel = game.GameLevel,
                    PlayerSkillLevel = game.PlayerSkillLevel,
                    RulesSet = game.RulesSet,
                    GenericField1 = game.GenericFields[0],
                    GenericField2 = game.GenericFields[1],
                    GenericField3 = game.GenericFields[2],
                    GenericField4 = game.GenericFields[3],
                    GenericField5 = game.GenericFields[4],
                    GenericField6 = game.GenericFields[5],
                    GenericField7 = game.GenericFields[6],
                    GenericField8 = game.GenericFields[7],
                    SecurityLevel = SecurityOf(game),
                    WorldStatus = game.Status,
                    GameHostType = game.HostType,
                    GameName = game.Name,
                    GameStats = game.Stats,
                    EndOfList = last,
                }, new MediusGameList_ExtraInfoResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
                return true;
            case MediusGameList_ExtraInfoRequest0 request:
                SendGameList(connection, client, request.PageID, request.PageSize, request.MessageID, (game, last) => new MediusGameList_ExtraInfoResponse0
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    MediusWorldID = game.Id,
                    PlayerCount = (ushort)game.PlayerCount,
                    MinPlayers = (ushort)game.MinPlayers,
                    MaxPlayers = (ushort)game.MaxPlayers,
                    GameLevel = game.GameLevel,
                    PlayerSkillLevel = game.PlayerSkillLevel,
                    RulesSet = game.RulesSet,
                    GenericField1 = game.GenericFields[0],
                    GenericField2 = game.GenericFields[1],
                    GenericField3 = game.GenericFields[2],
                    SecurityLevel = SecurityOf(game),
                    WorldStatus = game.Status,
                    GameHostType = game.HostType,
                    GameName = game.Name,
                    GameStats = game.Stats,
                    EndOfList = last,
                }, new MediusGameList_ExtraInfoResponse0 { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
                return true;

            case MediusGameInfoRequest request:
                connection.SendApp(Context.Lobby.Game(title, request.MediusWorldID) is not { } info
                    ? new MediusGameInfoResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusGameNotFound }
                    : new MediusGameInfoResponse
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        ApplicationID = info.AppId,
                        MinPlayers = info.MinPlayers,
                        MaxPlayers = info.MaxPlayers,
                        GameLevel = info.GameLevel,
                        PlayerSkillLevel = info.PlayerSkillLevel,
                        PlayerCount = info.PlayerCount,
                        GameStats = info.Stats,
                        GameName = info.Name,
                        RulesSet = info.RulesSet,
                        GenericField1 = info.GenericFields[0],
                        GenericField2 = info.GenericFields[1],
                        GenericField3 = info.GenericFields[2],
                        GenericField4 = info.GenericFields[3],
                        GenericField5 = info.GenericFields[4],
                        GenericField6 = info.GenericFields[5],
                        GenericField7 = info.GenericFields[6],
                        GenericField8 = info.GenericFields[7],
                        WorldStatus = info.Status,
                        GameHostType = info.HostType,
                    });
                return true;
            case MediusGameInfoRequest0 request:
                connection.SendApp(Context.Lobby.Game(title, request.MediusWorldID) is not { } info0
                    ? new MediusGameInfoResponse0 { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusGameNotFound }
                    : new MediusGameInfoResponse0
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        ApplicationID = info0.AppId,
                        MinPlayers = info0.MinPlayers,
                        MaxPlayers = info0.MaxPlayers,
                        GameLevel = info0.GameLevel,
                        PlayerSkillLevel = info0.PlayerSkillLevel,
                        PlayerCount = info0.PlayerCount,
                        GameStats = info0.Stats,
                        GameName = info0.Name,
                        RulesSet = info0.RulesSet,
                        GenericField1 = info0.GenericFields[0],
                        GenericField2 = info0.GenericFields[1],
                        GenericField3 = info0.GenericFields[2],
                        WorldStatus = info0.Status,
                        GameHostType = info0.HostType,
                    });
                return true;

            case MediusGameWorldPlayerListRequest request:
                SendGamePlayers(connection, title, request);
                return true;

            case MediusWorldReport report:
                if (ReportedGame(client, report.MediusWorldID) is { } reported)
                    reported.Report(report);
                return true;
            case MediusWorldReport0 report:
                if (ReportedGame(client, report.MediusWorldID) is { } reported0)
                    reported0.Report(report);
                return true;

            case MediusPlayerReport report:
                if (client.GameSeat is { } seat && seat.Game.Id == report.MediusWorldID && report.Stats is not null)
                    seat.Stats = report.Stats;
                return true;

            case MediusEndGameReport report:
                if (ReportedGame(client, report.MediusWorldID) is { } ended)
                {
                    ended.Close();
                    Log.Information("{Tag} {Game} ended by {Client} (winner {Winner}, score {Score})",
                        Tag, ended, client, string.IsNullOrEmpty(report.WinningTeam) ? report.WinningPlayer : report.WinningTeam, report.FinalScore);
                    if (ended.Players.Length == 0)
                        Context.Lobby.EndGame(ended);
                }
                return true;

            case MediusSetGameListFilterRequest request:
                connection.SendApp(new MediusSetGameListFilterResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    FilterID = AddFilter(client, request.FilterField, request.ComparisonOperator, request.BaselineValue, request.Mask),
                });
                return true;
            case MediusSetGameListFilterRequest0 request:
                connection.SendApp(new MediusSetGameListFilterResponse0
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    FilterID = AddFilter(client, request.FilterField, request.ComparisonOperator, request.BaselineValue, uint.MaxValue),
                });
                return true;

            case MediusClearGameListFilterRequest request:
                ClearFilter(client, request.FilterID);
                connection.SendApp(new MediusClearGameListFilterResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                return true;
            case MediusClearGameListFilterRequest0 request:
                ClearFilter(client, request.FilterID);
                connection.SendApp(new MediusClearGameListFilterResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                return true;

            case MediusGetGameListFilterRequest request:
                SendFilters(connection, client, request.MessageID);
                return true;
        }
        return false;
    }

    private void CreateGame(ScertConnection connection, MediusClient client, MessageId messageId, Func<int, MediusGame> create)
    {
        if (Context.Settings.PortOf(MediusComponentNames.Dme, udp: false) == 0)
        {
            Log.Error("{Tag} {Client} tried to create a game, but no DME listener is configured; add DME listeners to MediusConfig.json", Tag, client);
            connection.SendApp(new MediusCreateGameResponse { MessageID = messageId, StatusCode = MediusCallbackStatus.MediusFail, MediusWorldID = -1 });
            return;
        }

        MediusGame? game = Context.Lobby.CreateGame(client.Title, create);
        if (game is null)
        {
            connection.SendApp(new MediusCreateGameResponse { MessageID = messageId, StatusCode = MediusCallbackStatus.MediusGameNameExists, MediusWorldID = -1 });
            return;
        }
        Log.Information("{Tag} {Client} created {Game} ({Max} players, host type {HostType})", Tag, client, game, game.MaxPlayers, game.HostType);
        connection.SendApp(new MediusCreateGameResponse { MessageID = messageId, StatusCode = MediusCallbackStatus.MediusSuccess, MediusWorldID = game.Id });
    }

    private void JoinGame(ScertConnection connection, MediusClient client, MediusJoinGameRequest request)
    {
        MediusGame? game = Context.Lobby.Game(client.Title, request.MediusWorldID);
        string password = request.JoinType == MediusJoinType.MediusJoinAsPlayer ? game?.Password ?? "" : game?.SpectatorPassword ?? "";
        MediusCallbackStatus status =
            game is null || game.Status == MediusWorldStatus.WorldClosed ? MediusCallbackStatus.MediusGameNotFound
            : password.Length > 0 && password != request.GamePassword ? MediusCallbackStatus.MediusInvalidPassword
            : client.GameSeat?.Game != game && game.PlayerCount >= game.MaxPlayers ? MediusCallbackStatus.MediusWorldIsFull
            : MediusCallbackStatus.MediusSuccess;

        DmePlayer? seat = status == MediusCallbackStatus.MediusSuccess ? Context.Lobby.JoinGame(client, game!) : null;
        if (seat is null)
        {
            connection.SendApp(new MediusJoinGameResponse
            {
                MessageID = request.MessageID,
                StatusCode = status == MediusCallbackStatus.MediusSuccess ? MediusCallbackStatus.MediusWorldIsFull : status,
                ConnectInfo = new NetConnectionInfo(),
            });
            return;
        }

        Log.Information("{Tag} {Client} joining {Game} as player {Index}", Tag, client, seat.Game, seat.Index);
        connection.SendApp(new MediusJoinGameResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusSuccess,
            GameHostType = seat.Game.HostType,
            ConnectInfo = new NetConnectionInfo
            {
                Type = NetConnectionType.NetConnectionTypeClientServerTCPAuxUDP,
                AccessKey = seat.AccessKey,
                SessionKey = client.SessionKey,
                WorldID = seat.Game.Id,
                ServerKey = LobbyCommon.PublicKeyOf(Context.ServerKey),
                AddressList = new NetAddressList
                {
                    AddressList =
                    [
                        new NetAddress { Address = Context.PublicAddress.ToString(), Port = (uint)Context.Settings.PortOf(MediusComponentNames.Dme, udp: false), AddressType = NetAddressType.NetAddressTypeExternal },
                        new NetAddress { Address = Context.PublicAddress.ToString(), Port = (uint)Context.Settings.PortOf(MediusComponentNames.Nat), AddressType = NetAddressType.NetAddressTypeNATService },
                    ],
                },
            },
        });
    }

    /// <summary>Sends one page of the games the player's filters let through, oldest first.</summary>
    private void SendGameList<T>(ScertConnection connection, MediusClient client, int pageId, int pageSize, MessageId messageId,
        Func<MediusGame, bool, T> entry, T none) where T : BaseMediusMessage
    {
        if (pageSize <= 0)
            pageSize = DefaultGameListPageSize;
        GameListFilter[] filters;
        lock (client.GameListFilters)
            filters = [.. client.GameListFilters];
        MediusGame[] games = Context.Lobby.Games(client.Title)
            .Where(g => g.IsListed && filters.All(f => f.IsMatch(g)))
            .Skip((Math.Max(pageId, 1) - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        if (games.Length == 0)
        {
            connection.SendApp(none);
            return;
        }
        for (int i = 0; i < games.Length; i++)
            connection.SendApp(entry(games[i], i == games.Length - 1));
    }

    private void SendGamePlayers(ScertConnection connection, MediusTitle title, MediusGameWorldPlayerListRequest request)
    {
        MediusGame? game = Context.Lobby.Game(title, request.MediusWorldID);
        DmePlayer[] players = game?.JoinedPlayers ?? [];
        if (players.Length == 0)
        {
            connection.SendApp(new MediusGameWorldPlayerListResponse
            {
                MessageID = request.MessageID,
                StatusCode = game is null ? MediusCallbackStatus.MediusGameNotFound : MediusCallbackStatus.MediusNoResult,
                EndOfList = true,
            });
            return;
        }
        for (int i = 0; i < players.Length; i++)
        {
            connection.SendApp(new MediusGameWorldPlayerListResponse
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusSuccess,
                AccountID = players[i].Client.AccountId,
                AccountName = players[i].Client.AccountName,
                Stats = players[i].Stats,
                ConnectionClass = MediusConnectionType.Ethernet,
                EndOfList = i == players.Length - 1,
            });
        }
    }

    /// <summary>The game a report is about, if the reporter is its host or seated in it.</summary>
    private MediusGame? ReportedGame(MediusClient client, int worldId) =>
        Context.Lobby.Game(client.Title, worldId) is { } game && (game.Host == client || client.Game == game) ? game : null;

    private static MediusWorldSecurityLevelType SecurityOf(MediusGame game) =>
        game.HasPassword ? MediusWorldSecurityLevelType.WORLD_SECURITY_PLAYER_PASSWORD : MediusWorldSecurityLevelType.WORLD_SECURITY_NONE;

    private static uint AddFilter(MediusClient client, MediusGameListFilterField field, MediusComparisonOperator op, int baseline, uint mask)
    {
        lock (client.GameListFilters)
        {
            GameListFilter filter = new() { Id = client.NextGameListFilterId++, Field = field, Operator = op, BaselineValue = baseline, Mask = mask };
            client.GameListFilters.Add(filter);
            return filter.Id;
        }
    }

    private static void ClearFilter(MediusClient client, uint filterId)
    {
        lock (client.GameListFilters)
            client.GameListFilters.RemoveAll(f => f.Id == filterId);
    }

    private static void SendFilters(ScertConnection connection, MediusClient client, MessageId messageId)
    {
        GameListFilter[] filters;
        lock (client.GameListFilters)
            filters = [.. client.GameListFilters];
        if (filters.Length == 0)
        {
            connection.SendApp(new MediusGetGameListFilterResponse { MessageID = messageId, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
            return;
        }
        for (int i = 0; i < filters.Length; i++)
        {
            connection.SendApp(new MediusGetGameListFilterResponse
            {
                MessageID = messageId,
                StatusCode = MediusCallbackStatus.MediusSuccess,
                FilterID = filters[i].Id,
                FilterField = filters[i].Field,
                Mask = filters[i].Mask,
                ComparisonOperator = filters[i].Operator,
                BaselineValue = filters[i].BaselineValue,
                EndOfList = i == filters.Length - 1,
            });
        }
    }
}
