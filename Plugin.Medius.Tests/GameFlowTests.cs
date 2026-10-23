using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;

namespace Plugin.Medius.Tests;

/// <summary>
/// Game worlds end to end: players log in, one creates a game on the MLS,
/// both join it, connect to the DME, and talk through it over TCP and UDP.
/// </summary>
[Collection(MediusServerCollection.Name)]
public sealed class GameFlowTests(LobbyFlowFixture fixture)
{
    private const int Ps3 = 113;
    private const int AppId = LobbyFlowFixture.AppId;

    [Fact]
    public async Task Two_players_create_join_and_relay_through_the_dme()
    {
        await using Player alice = await Player.LoginAsync(fixture, "alice-dme");
        await using Player bob = await Player.LoginAsync(fixture, "bob-dme");

        // Alice creates and joins; the game is unlisted until she reports it open.
        int worldId = await alice.CreateGameAsync("Relay Test", maxPlayers: 4);
        MediusJoinGameResponse aliceJoin = await alice.JoinGameAsync(worldId);
        Assert.Equal(MediusCallbackStatus.MediusSuccess, aliceJoin.StatusCode);
        Assert.Equal(NetConnectionType.NetConnectionTypeClientServerTCPAuxUDP, aliceJoin.ConnectInfo.Type);
        Assert.Equal((uint)fixture.DmePort, aliceJoin.ConnectInfo.AddressList.AddressList[0].Port);
        Assert.DoesNotContain(await bob.ListGamesAsync(), g => g.MediusWorldID == worldId);

        await using MediusTestClient aliceDme = new(Ps3);
        await aliceDme.ConnectAsync(fixture.DmePort);
        RT_MSG_SERVER_CONNECT_ACCEPT_TCP aliceAccept = await aliceDme.DmeHandshakeAsync(AppId, aliceJoin.ConnectInfo);
        await alice.ReportAsync(new MediusWorldReport
        {
            MediusWorldID = worldId,
            GameName = "Relay Test",
            MaxPlayers = 4,
            GenericField1 = 7,
            WorldStatus = MediusWorldStatus.WorldStaging,
        });

        // Bob finds it, joins, connects; Alice is told he arrived.
        MediusGameList_ExtraInfoResponse listed = Assert.Single(await bob.ListGamesAsync(), g => g.MediusWorldID == worldId);
        Assert.Equal("Relay Test", listed.GameName);
        Assert.Equal((ushort)1, listed.PlayerCount);
        Assert.Equal(7, listed.GenericField1);

        MediusJoinGameResponse bobJoin = await bob.JoinGameAsync(worldId);
        Assert.Equal(MediusCallbackStatus.MediusSuccess, bobJoin.StatusCode);
        await using MediusTestClient bobDme = new(Ps3);
        await bobDme.ConnectAsync(fixture.DmePort);
        RT_MSG_SERVER_CONNECT_ACCEPT_TCP bobAccept = await bobDme.DmeHandshakeAsync(AppId, bobJoin.ConnectInfo);
        Assert.NotEqual(aliceAccept.PlayerId, bobAccept.PlayerId);

        RT_MSG_SERVER_CONNECT_NOTIFY arrived = await aliceDme.ExpectAsync<RT_MSG_SERVER_CONNECT_NOTIFY>();
        Assert.Equal((short)bobAccept.PlayerId, arrived.PlayerIndex);

        // TCP broadcast from Bob reaches Alice as a single from Bob.
        bobDme.Send(new RT_MSG_CLIENT_APP_BROADCAST { Payload = [1, 2, 3] });
        RT_MSG_CLIENT_APP_SINGLE broadcast = await aliceDme.ExpectAsync<RT_MSG_CLIENT_APP_SINGLE>();
        Assert.Equal((short)bobAccept.PlayerId, broadcast.TargetOrSource);
        Assert.Equal(new byte[] { 1, 2, 3 }, broadcast.Payload);

        // TCP single and list from Alice to Bob.
        aliceDme.Send(new RT_MSG_CLIENT_APP_SINGLE { TargetOrSource = (short)bobAccept.PlayerId, Payload = [4, 5] });
        RT_MSG_CLIENT_APP_SINGLE single = await bobDme.ExpectAsync<RT_MSG_CLIENT_APP_SINGLE>();
        Assert.Equal((short)aliceAccept.PlayerId, single.TargetOrSource);
        Assert.Equal(new byte[] { 4, 5 }, single.Payload);

        aliceDme.Send(new RT_MSG_CLIENT_APP_LIST { Targets = [bobAccept.PlayerId], Payload = [6] });
        Assert.Equal(new byte[] { 6 }, (await bobDme.ExpectAsync<RT_MSG_CLIENT_APP_SINGLE>()).Payload);

        // UDP goes out over UDP.
        bobDme.SendUdp(new RT_MSG_CLIENT_APP_BROADCAST { Payload = [7, 8] });
        RT_MSG_CLIENT_APP_SINGLE udp = await aliceDme.ExpectUdpAsync<RT_MSG_CLIENT_APP_SINGLE>();
        Assert.Equal((short)bobAccept.PlayerId, udp.TargetOrSource);
        Assert.Equal(new byte[] { 7, 8 }, udp.Payload);

        // The MLS sees both players in the world.
        bob.Mls.SendApp(new MediusGameWorldPlayerListRequest { MessageID = new MessageId("pl"), MediusWorldID = worldId });
        MediusGameWorldPlayerListResponse first = await bob.Mls.ExpectAppAsync<MediusGameWorldPlayerListResponse>();
        MediusGameWorldPlayerListResponse second = await bob.Mls.ExpectAppAsync<MediusGameWorldPlayerListResponse>();
        Assert.Equal(new[] { "alice-dme", "bob-dme" }, new[] { first.AccountName, second.AccountName }.Order());
        Assert.True(second.EndOfList);

        // Bob leaves; Alice is told.
        await bobDme.DisposeAsync();
        RT_MSG_SERVER_DISCONNECT_NOTIFY left = await aliceDme.ExpectAsync<RT_MSG_SERVER_DISCONNECT_NOTIFY>();
        Assert.Equal((short)bobAccept.PlayerId, left.PlayerIndex);
        Assert.Equal((ushort)1, Assert.Single(await bob.ListGamesAsync(), g => g.MediusWorldID == worldId).PlayerCount);

        // Alice ends the game and leaves; it is gone.
        alice.Mls.SendApp(new MediusEndGameReport { MediusWorldID = worldId, WinningPlayer = "alice-dme" });
        await aliceDme.DisposeAsync();
        await WaitUntil(() => fixture.Plugin.Context!.Lobby.GameByWorldId(worldId) is null);
        alice.Mls.SendApp(new MediusGameInfoRequest { MessageID = new MessageId("gi"), MediusWorldID = worldId });
        Assert.Equal(MediusCallbackStatus.MediusGameNotFound, (await alice.Mls.ExpectAppAsync<MediusGameInfoResponse>()).StatusCode);
    }

    [Fact]
    public async Task Join_checks_password_and_existence_and_filters_narrow_the_list()
    {
        await using Player carol = await Player.LoginAsync(fixture, "carol-dme");
        await using Player dave = await Player.LoginAsync(fixture, "dave-dme");

        int worldId = await carol.CreateGameAsync("Locked Room", maxPlayers: 2, password: "sesame", genericField1: 3);
        Assert.Equal(MediusCallbackStatus.MediusGameNameExists, (await carol.TryCreateGameAsync("Locked Room", 2)).StatusCode);
        await carol.ReportAsync(new MediusWorldReport { MediusWorldID = worldId, GameName = "Locked Room", MaxPlayers = 2, GenericField1 = 3, WorldStatus = MediusWorldStatus.WorldStaging });

        Assert.Equal(MediusCallbackStatus.MediusGameNotFound, (await dave.JoinGameAsync(999_999)).StatusCode);
        Assert.Equal(MediusCallbackStatus.MediusInvalidPassword, (await dave.JoinGameAsync(worldId, "wrong")).StatusCode);
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await dave.JoinGameAsync(worldId, "sesame")).StatusCode);

        MediusGameList_ExtraInfoResponse listed = Assert.Single(await dave.ListGamesAsync(), g => g.MediusWorldID == worldId);
        Assert.Equal(MediusWorldSecurityLevelType.WORLD_SECURITY_PLAYER_PASSWORD, listed.SecurityLevel);

        // A filter the game fails hides it; clearing the filter shows it again.
        dave.Mls.SendApp(new MediusSetGameListFilterRequest
        {
            MessageID = new MessageId("f1"),
            FilterField = MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_1,
            ComparisonOperator = MediusComparisonOperator.EQUAL_TO,
            BaselineValue = 4,
            Mask = uint.MaxValue,
        });
        MediusSetGameListFilterResponse set = await dave.Mls.ExpectAppAsync<MediusSetGameListFilterResponse>();
        Assert.Equal(MediusCallbackStatus.MediusSuccess, set.StatusCode);
        Assert.DoesNotContain(await dave.ListGamesAsync(), g => g.MediusWorldID == worldId);

        dave.Mls.SendApp(new MediusGetGameListFilterRequest { MessageID = new MessageId("f2") });
        MediusGetGameListFilterResponse filter = await dave.Mls.ExpectAppAsync<MediusGetGameListFilterResponse>();
        Assert.Equal(set.FilterID, filter.FilterID);
        Assert.True(filter.EndOfList);

        dave.Mls.SendApp(new MediusClearGameListFilterRequest { MessageID = new MessageId("f3"), FilterID = set.FilterID });
        await dave.Mls.ExpectAppAsync<MediusClearGameListFilterResponse>();
        Assert.Contains(await dave.ListGamesAsync(), g => g.MediusWorldID == worldId);
    }

    [Fact]
    public async Task Dme_refuses_an_access_key_it_never_issued()
    {
        await using MediusTestClient dme = new(Ps3);
        await dme.ConnectAsync(fixture.DmePort);
        await Assert.ThrowsAnyAsync<Exception>(() => dme.DmeHandshakeAsync(AppId, new NetConnectionInfo { WorldID = 1, AccessKey = "0000000000000000", SessionKey = "1" }));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 50 && !condition(); i++)
            await Task.Delay(100);
        Assert.True(condition());
    }

    /// <summary>A logged-in player sitting in the lobby.</summary>
    private sealed class Player : IAsyncDisposable
    {
        private int _messageId;

        private Player(MediusTestClient mls, string sessionKey)
        {
            Mls = mls;
            SessionKey = sessionKey;
        }

        public MediusTestClient Mls { get; }
        public string SessionKey { get; }

        public static async Task<Player> LoginAsync(LobbyFlowFixture fixture, string name)
        {
            await using MediusTestClient mas = new(Ps3);
            await mas.ConnectAsync(fixture.MasPort);
            await mas.HandshakeAsync(AppId);
            mas.SendApp(new MediusSessionBeginRequest { MessageID = new MessageId("s"), ConnectionClass = MediusConnectionType.Ethernet });
            MediusSessionBeginResponse session = await mas.ExpectAppAsync<MediusSessionBeginResponse>();
            mas.SendApp(new MediusTicketLoginRequest { MessageID = new MessageId("l"), SessionKey = session.SessionKey, Username = name });
            MediusTicketLoginResponse login = await mas.ExpectAppAsync<MediusTicketLoginResponse>();
            Assert.Equal(MediusCallbackStatus.MediusSuccess, login.StatusCode);

            MediusTestClient mls = new(Ps3);
            await mls.ConnectAsync(fixture.MlsPort);
            await mls.HandshakeAsync(AppId, session.SessionKey, login.ConnectInfo.AccessKey, (uint)login.MediusWorldID);
            return new Player(mls, session.SessionKey);
        }

        public async Task<MediusCreateGameResponse> TryCreateGameAsync(string name, int maxPlayers, string password = "", int genericField1 = 0)
        {
            Mls.SendApp(new MediusCreateGameRequest
            {
                MessageID = NextId(),
                SessionKey = SessionKey,
                ApplicationID = AppId,
                MinPlayers = 1,
                MaxPlayers = maxPlayers,
                GameName = name,
                GamePassword = password,
                SpectatorPassword = "",
                GenericField1 = genericField1,
                GameHostType = MediusGameHostType.MediusGameHostClientServerAuxUDP,
            });
            return await Mls.ExpectAppAsync<MediusCreateGameResponse>();
        }

        public async Task<int> CreateGameAsync(string name, int maxPlayers, string password = "", int genericField1 = 0)
        {
            MediusCreateGameResponse created = await TryCreateGameAsync(name, maxPlayers, password, genericField1);
            Assert.Equal(MediusCallbackStatus.MediusSuccess, created.StatusCode);
            return created.MediusWorldID;
        }

        public async Task<MediusJoinGameResponse> JoinGameAsync(int worldId, string password = "")
        {
            Mls.SendApp(new MediusJoinGameRequest
            {
                MessageID = NextId(),
                SessionKey = SessionKey,
                MediusWorldID = worldId,
                JoinType = MediusJoinType.MediusJoinAsPlayer,
                GamePassword = password,
                GameHostType = MediusGameHostType.MediusGameHostClientServerAuxUDP,
                pubKey = new RSA_KEY(),
                AddressList = new NetAddressList(),
            });
            return await Mls.ExpectAppAsync<MediusJoinGameResponse>();
        }

        /// <summary>Sends a world report, which has no answer, then a game info request so it has been handled before returning.</summary>
        public async Task ReportAsync(MediusWorldReport report)
        {
            Mls.SendApp(report);
            Mls.SendApp(new MediusGameInfoRequest { MessageID = NextId(), MediusWorldID = report.MediusWorldID });
            MediusGameInfoResponse info = await Mls.ExpectAppAsync<MediusGameInfoResponse>();
            Assert.Equal(report.WorldStatus, info.WorldStatus);
        }

        public async Task<List<MediusGameList_ExtraInfoResponse>> ListGamesAsync()
        {
            Mls.SendApp(new MediusGameList_ExtraInfoRequest { MessageID = NextId(), PageID = 1, PageSize = 50 });
            List<MediusGameList_ExtraInfoResponse> games = [];
            while (true)
            {
                MediusGameList_ExtraInfoResponse entry = await Mls.ExpectAppAsync<MediusGameList_ExtraInfoResponse>();
                if (entry.StatusCode == MediusCallbackStatus.MediusSuccess)
                    games.Add(entry);
                if (entry.EndOfList)
                    return games;
            }
        }

        private MessageId NextId() => new($"g{++_messageId}");

        public ValueTask DisposeAsync() => Mls.DisposeAsync();
    }
}
