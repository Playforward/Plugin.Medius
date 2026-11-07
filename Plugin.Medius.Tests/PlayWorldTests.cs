using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;
using Player = Plugin.Medius.Tests.GameFlowTests.Player;

namespace Plugin.Medius.Tests;

/// <summary>
/// The SCE-RT 3.03 game-world messages Killzone 2 plays through: after
/// joining its listed game the host creates a play world "&lt;name&gt;P"
/// (lobby-ext 0x65), finds it by name (lobby 0x37) and joins it (lobby-ext
/// 0x87 -> 0x8E); joining players do the same, and "create" answers them with
/// the open world. Layouts are the 1.29 client's (registration 0x720bb0..).
/// </summary>
[Collection(MediusServerCollection.Name)]
public sealed class PlayWorldTests(LobbyFlowFixture fixture)
{
    private const int Ps3 = 113;
    private const int AppHeader = 2;

    private static byte[] AppBytes(BaseMediusMessage message, bool fromClient)
    {
        using MemoryStream stream = new();
        using (MessageWriter writer = new(stream) { MediusVersion = Ps3 })
        {
            if (fromClient)
                new RT_MSG_CLIENT_APP_TOSERVER { Message = message }.Serialize(writer);
            else
                new RT_MSG_SERVER_APP { Message = message }.Serialize(writer);
        }
        return stream.ToArray();
    }

    private static BaseMediusMessage Read(byte[] bytes, bool fromClient)
    {
        using MessageReader reader = new(new MemoryStream(bytes)) { MediusVersion = Ps3 };
        return BaseMediusMessage.Instantiate(reader, fromClient);
    }

    [Fact]
    public void Sizes_are_the_clients()
    {
        Assert.Equal(AppHeader + 184, AppBytes(new MediusCreateGameRequest2 { SessionKey = "", GameName = "", GamePassword = "" }, true).Length);
        Assert.Equal(AppHeader + 32, AppBytes(new MediusCreateGameResponse2(), false).Length);
        Assert.Equal(AppHeader + 54, AppBytes(new MediusGameCountRequest { SessionKey = "" }, true).Length);
        Assert.Equal(AppHeader + 29, AppBytes(new MediusGameCountResponse(), false).Length);
        Assert.Equal(AppHeader + 108, AppBytes(new MediusFindWorldByNameRequest { SessionKey = "", Name = "" }, true).Length);
        Assert.Equal(AppHeader + 144, AppBytes(new MediusFindWorldByNameResponse { ApplicationName = "", WorldName = "" }, false).Length);
        Assert.Equal(AppHeader + 196, AppBytes(new MediusJoinGameRequest2 { SessionKey = "", GamePassword = "" }, true).Length);
        Assert.Equal(AppHeader + 192, AppBytes(new MediusJoinGameResponse2(), false).Length);
    }

    [Fact]
    public void Live_create_request_reads_back()
    {
        // Killzone 2's lobby-ext 0x65 after creating "Warzone~1838689002" [live 2026-10-31].
        byte[] content = new byte[184];
        "24"u8.CopyTo(content);
        "13008"u8.CopyTo(content.AsSpan(0x15));
        BitConverter.GetBytes(21784).CopyTo(content, 0x28);
        BitConverter.GetBytes(32).CopyTo(content, 0x30);
        "Warzone~1838689002P"u8.CopyTo(content.AsSpan(0x34));
        BitConverter.GetBytes(4).CopyTo(content, 0xb4);

        var request = Assert.IsType<MediusCreateGameRequest2>(Read([(byte)NetMessageTypes.MessageClassLobbyExt, 0x65, .. content], fromClient: true));
        Assert.Equal("13008", request.SessionKey);
        Assert.Equal(21784, request.ApplicationID);
        Assert.Equal(32, request.MaxPlayers);
        Assert.Equal("Warzone~1838689002P", request.GameName);
        Assert.Equal(MediusGameHostType.MediusGameHostClientServerAuxUDP, request.GameHostType);
    }

    [Fact]
    public void Lobby_ext_0x87_is_a_join_from_a_client_and_a_filter_answer_from_a_server()
    {
        byte[] join = AppBytes(new MediusJoinGameRequest2 { MessageID = new MessageId("25"), SessionKey = "k", MediusWorldID = 742, GamePassword = "" }, true);
        Assert.IsType<MediusJoinGameRequest2>(Read(join, fromClient: true));
        Assert.IsType<MediusSetLobbyWorldFilterResponse1>(Read(join, fromClient: false));
    }

    [Fact]
    public void Live_join_answer_reads_back()
    {
        // PS Rewired's lobby-ext 0x8E to Killzone 2 (proxy log 2026-10-31 00:27:56) [live].
        byte[] bytes = Convert.FromHexString(
            "048E3235000000000000000000000000000000000000000000000000000004000000030000000100000036372E3232322E3135362E3235300000F92A00000300000036372E3232322E3135362E323530000056270000E6020000" +
            new string('0', 128) + "633130616437656632643234646566390064386230343933663366366261376633000000" + "02000000");
        var response = Assert.IsType<MediusJoinGameResponse2>(Read(bytes, fromClient: false));
        Assert.Equal(MediusCallbackStatus.MediusSuccess, response.StatusCode);
        Assert.Equal(MediusGameHostType.MediusGameHostClientServerAuxUDP, response.GameHostType);
        Assert.Equal(742, response.ConnectInfo.WorldID);
        Assert.Equal(11001u, response.ConnectInfo.AddressList.AddressList[0].Port);
        Assert.Equal("c10ad7ef2d24def9", response.ConnectInfo.SessionKey);
        Assert.Equal(2, response.Unknown_BC);
    }

    [Fact]
    public async Task Host_and_joiner_create_find_and_join_the_play_world()
    {
        await using Player host = await Player.LoginAsync(fixture, "kz-host");
        await using Player joiner = await Player.LoginAsync(fixture, "kz-joiner");

        // The host's listed game, and its DME connection, which it keeps.
        int listed = await host.CreateGameAsync("Warzone~1", maxPlayers: 40);
        MediusJoinGameResponse listedJoin = await host.JoinGameAsync(listed);
        await using MediusTestClient listedDme = new(Ps3);
        await listedDme.ConnectAsync(fixture.DmePort);
        await listedDme.DmeHandshakeAsync(LobbyFlowFixture.AppId, listedJoin.ConnectInfo);

        MediusCreateGameResponse2 created = await CreatePlayWorldAsync(host, "Warzone~1P");
        Assert.Equal(MediusCallbackStatus.MediusSuccess, created.StatusCode);
        int playWorld = Assert.Single(await FindAsync(host, "Warzone~1P")).MediusWorldID;
        Assert.Equal(created.MediusWorldID, playWorld);

        MediusJoinGameResponse2 hostJoin = await JoinPlayWorldAsync(host, playWorld);
        Assert.Equal(MediusCallbackStatus.MediusSuccess, hostJoin.StatusCode);
        Assert.Equal(playWorld, hostJoin.ConnectInfo.WorldID);
        Assert.Equal((uint)fixture.DmePort, hostJoin.ConnectInfo.AddressList.AddressList[0].Port);
        await using MediusTestClient hostDme = new(Ps3);
        await hostDme.ConnectAsync(fixture.DmePort);
        await hostDme.DmeHandshakeAsync(LobbyFlowFixture.AppId, hostJoin.ConnectInfo);

        // The listed game still has the host.
        Assert.Equal(1, fixture.Plugin.Context!.Lobby.GameByWorldId(listed)!.PlayerCount);

        // The joiner's "create" finds the open world.
        MediusCreateGameResponse2 again = await CreatePlayWorldAsync(joiner, "Warzone~1P");
        Assert.Equal(MediusCallbackStatus.MediusGameNameExists, again.StatusCode);
        Assert.Equal(playWorld, again.MediusWorldID);
        MediusFindWorldByNameResponse found = Assert.Single(await FindAsync(joiner, "warzone~1p"));
        Assert.True(found.EndOfList);
        Assert.Equal(MediusApplicationType.MediusAppTypeGame, found.ApplicationType);
        MediusJoinGameResponse2 joinerJoin = await JoinPlayWorldAsync(joiner, found.MediusWorldID);
        Assert.Equal(MediusCallbackStatus.MediusSuccess, joinerJoin.StatusCode);
        await using MediusTestClient joinerDme = new(Ps3);
        await joinerDme.ConnectAsync(fixture.DmePort);
        await joinerDme.DmeHandshakeAsync(LobbyFlowFixture.AppId, joinerJoin.ConnectInfo);
        Assert.IsType<RT_MSG_SERVER_CONNECT_NOTIFY>(await hostDme.ExpectAsync<RT_MSG_SERVER_CONNECT_NOTIFY>());

        Assert.Empty(await FindAsync(joiner, "No Such World"));
    }

    [Fact]
    public void Live_game_count_request_reads_back()
    {
        // The Deck's lobby-ext 0x7E before its game list [live 2026-10-31 22:20]: 54 bytes, no pad.
        byte[] bytes = Convert.FromHexString("047E3139000000000000000000000000000000000000003134333930000000000000000000000000010000000100000" +
                                             "0FFFFFFFF01000000");
        MediusGameCountRequest request = Assert.IsType<MediusGameCountRequest>(Read(bytes, fromClient: true));
        Assert.Equal(("19", "14390", 1, 1, -1, 1), (request.MessageID.Value, request.SessionKey, request.Field1, request.Field2, request.Field3, request.Field4));
    }

    [Fact]
    public void Killzone2_filters_list_a_hosts_reported_game()
    {
        // The Deck's filters [live 2026-10-31 22:28] against the PC host's world report (fields 1..8 = 0, 1, 0,
        // 0x20, 0x1F, 1, ..; GameLevel 255; skill 0).
        GameListFilter[] filters =
        [
            new() { Field = MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_5, Operator = MediusComparisonOperator.GREATER_THAN, BaselineValue = 0 },
            new() { Field = MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_6, Operator = MediusComparisonOperator.EQUAL_TO, BaselineValue = 1 },
            new() { Field = MediusGameListFilterField.MEDIUS_FILTER_PLAYER_SKILL_LEVEL, Operator = MediusComparisonOperator.EQUAL_TO, BaselineValue = 0 },
            new() { Field = MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_1, Operator = MediusComparisonOperator.EQUAL_TO, BaselineValue = 0, Mask = 2 },
            new() { Field = MediusGameListFilterField.MEDIUS_FILTER_GAME_LEVEL, Operator = MediusComparisonOperator.EQUAL_TO, BaselineValue = 0, Mask = 0xFF00 },
            new() { Field = MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_3, Operator = MediusComparisonOperator.EQUAL_TO, BaselineValue = 0 },
        ];
        MediusTitle title = new();
        MediusClient host = new(title, 21784, 113);
        MediusGame listed = new(1, title, host, new MediusCreateGameRequest
        {
            GameName = "Warzone~1", GamePassword = "", SpectatorPassword = "", MaxPlayers = 40, GameLevel = 255,
            GenericField2 = 1, GenericField4 = 0x20, GenericField5 = 0x1F, GenericField6 = 1,
        });
        MediusGame playWorld = new(2, title, host, new MediusCreateGameRequest { GameName = "Warzone~1P", GamePassword = "", SpectatorPassword = "" });
        Assert.All(filters, f => Assert.True(f.IsMatch(listed), f.Field.ToString()));
        Assert.False(filters[0].IsMatch(playWorld));
    }

    [Fact]
    public async Task Game_count_is_answered()
    {
        await using Player player = await Player.LoginAsync(fixture, "kz-counter");
        player.Mls.SendApp(new MediusGameCountRequest { MessageID = new MessageId("20"), SessionKey = player.SessionKey, Field1 = 1, Field2 = 1, Field3 = -1, Field4 = 1 });
        MediusGameCountResponse count = await player.Mls.ExpectAppAsync<MediusGameCountResponse>();
        Assert.Equal(MediusCallbackStatus.MediusSuccess, count.StatusCode);
        Assert.True(count.Count >= 0);
    }

    private static async Task<MediusCreateGameResponse2> CreatePlayWorldAsync(Player player, string name)
    {
        player.Mls.SendApp(new MediusCreateGameRequest2
        {
            MessageID = new MessageId("c2"),
            SessionKey = player.SessionKey,
            ApplicationID = LobbyFlowFixture.AppId,
            MaxPlayers = 32,
            GameName = name,
            GamePassword = "",
            GameHostType = MediusGameHostType.MediusGameHostClientServerAuxUDP,
        });
        return await player.Mls.ExpectAppAsync<MediusCreateGameResponse2>();
    }

    private static async Task<List<MediusFindWorldByNameResponse>> FindAsync(Player player, string name)
    {
        player.Mls.SendApp(new MediusFindWorldByNameRequest { MessageID = new MessageId("fw"), SessionKey = player.SessionKey, Name = name, WorldType = MediusFindWorldType.FindAllWorlds });
        List<MediusFindWorldByNameResponse> found = [];
        while (true)
        {
            MediusFindWorldByNameResponse entry = await player.Mls.ExpectAppAsync<MediusFindWorldByNameResponse>();
            if (entry.StatusCode == MediusCallbackStatus.MediusSuccess)
                found.Add(entry);
            if (entry.EndOfList)
                return found;
        }
    }

    private static async Task<MediusJoinGameResponse2> JoinPlayWorldAsync(Player player, int worldId)
    {
        player.Mls.SendApp(new MediusJoinGameRequest2
        {
            MessageID = new MessageId("j2"),
            SessionKey = player.SessionKey,
            MediusWorldID = worldId,
            GamePassword = "",
            GameHostType = MediusGameHostType.MediusGameHostClientServerAuxUDP,
            pubKey = new RSA_KEY(),
            AddressList = new NetAddressList(),
        });
        return await player.Mls.ExpectAppAsync<MediusJoinGameResponse2>();
    }
}
