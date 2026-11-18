using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;

namespace Plugin.Medius.Tests;

/// <summary>Fall of Man's ranked messages against the client's codecs and what PSRewired sent (2026-11-01).</summary>
public sealed class ResistanceFallOfManRankedMessageTests
{
    private const int Ps3 = 113;

    private static byte[] Write(BaseMediusMessage message)
    {
        using MemoryStream stream = new();
        using (MessageWriter writer = new(stream) { MediusVersion = Ps3 })
            new RT_MSG_SERVER_APP { Message = message }.Serialize(writer);
        return stream.ToArray();
    }

    private static T Read<T>(string hex, bool fromClient) where T : BaseMediusMessage
    {
        using MessageReader reader = new(new MemoryStream(Convert.FromHexString(hex))) { MediusVersion = Ps3 };
        return Assert.IsType<T>(BaseMediusMessage.Instantiate(reader, fromClient));
    }

    [Fact]
    public void The_request_for_a_match_server_has_no_extra_data()
    {
        var m = Read<MediusMatchFindGameRequest>("0478323900000000000000000000000000000000000000346635623261636234656235376466360005000000010000000000000000000000000000000000000030760100", true);
        Assert.Equal((5u, 95792), (m.SupersetID, Assert.Single(m.AccountIDs)));
        Assert.Empty(m.Extra);
    }

    [Fact]
    public void A_referral_reads_and_writes_as_psrewired_sent_it()
    {
        const string hex = "046032390000000000000000000000000000000000000008000000050000000500000001000000010000003139322E3136382E322E3130330000005F2700000000000000000000000000000000000000000000000000000500000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000346635623261636234656235376466360032363465633037383630313134343530000000";
        var m = Read<MediusMatchFindGameResponse>(hex, false);
        Assert.Equal(MediusCallbackStatus.MediusMatchTypeReferral, m.StatusCode);
        Assert.Equal((5u, 5u), (m.Unk1C, m.Unk20));
        Assert.Equal(5, m.ConnectInfo.WorldID);
        Assert.Equal("264ec07860114450", m.ConnectInfo.AccessKey);
        Assert.Equal(hex, Convert.ToHexString(Write(m)));
    }

    [Fact]
    public void An_assigned_game_is_laid_out_like_the_codec_reads_it()
    {
        byte[] bytes = Write(new MediusMatchFindGameResponse
        {
            MessageID = new MessageId("m"),
            StatusCode = MediusCallbackStatus.MediusJoinAssignedGame,
            Unk1C = 1,
            GameInfo = new MediusMatchGameInfo
            {
                MediusWorldID = 77,
                MatchName = "RANKED-x",
                GameOptions = [0x02],
                Teams = [new MediusMatchTeam { Members = [new MediusMatchTeamMember { AccountID = 9 }] }],
            },
        });

        // 0x656e4c after class, type, MessageID[21], status and the u32: world, name[32], u32, addresses, options, roster.
        const int world = 2 + 21 + 8;
        Assert.Equal(77, BitConverter.ToInt32(bytes, world));
        Assert.Equal("RANKED-x"u8.ToArray(), bytes[(world + 4)..(world + 12)]);
        int options = world + 4 + 32 + 4 + 48;
        Assert.Equal(1, BitConverter.ToInt32(bytes, options));
        Assert.Equal(0x02, bytes[options + 4]);
        // Roster size as 0x656ca4 measures it, then one team of one member, account id first (0x656b80 reads it into +4, where 0x32debc takes it).
        int roster = options + 5;
        Assert.Equal(8 + 4 + 8 + 8, BitConverter.ToInt32(bytes, roster));
        Assert.Equal(1, BitConverter.ToInt32(bytes, roster + 4));
        Assert.Equal(1, BitConverter.ToInt32(bytes, roster + 12));
        Assert.Equal(9, BitConverter.ToInt32(bytes, roster + 16));
        Assert.Equal(roster + 24, bytes.Length);
    }

    [Fact]
    public void A_channel_answer_has_its_connect_info_at_the_codecs_offset()
    {
        byte[] bytes = Write(new MediusJoinLeastPopulatedChannelResponse
        {
            MessageID = new MessageId("m"),
            StatusCode = MediusCallbackStatus.MediusSuccess,
            ConnectInfo = new NetConnectionInfo { Type = NetConnectionType.NetConnectionTypeClientServerTCP, WorldID = 3 },
        });
        Assert.Equal((int)NetConnectionType.NetConnectionTypeClientServerTCP, BitConverter.ToInt32(bytes, 2 + 21 + 8));
    }

    [Fact]
    public void A_host_answer_keeps_its_target_after_the_qos_byte()
    {
        using MessageReader reader = new(new MemoryStream(Convert.FromHexString("50010000100000"))) { MediusVersion = Ps3 };
        RT_MSG_CLIENT_APP_SINGLE_QOS m = new();
        m.Deserialize(reader);
        Assert.Equal((0x50, (short)1), (m.QosFlags, m.Target));
        Assert.Equal(new byte[] { 0, 0x10, 0, 0 }, m.Payload);
    }

    [Fact]
    public void A_waiting_party_expires_after_the_titles_wait()
    {
        MediusTitle title = new() { Matchmaking = new MediusMatchmakingSettings { FindGameWaitSeconds = 30 } };
        MediusClient client = new(title, 1, 113);
        MediusMatchmaker matchmaker = new();
        matchmaker.Enqueue(client, new MediusMatchFindGameRequest { MessageID = new MessageId("m"), SupersetID = 1, AccountIDs = [1], Extra = [0] });

        Assert.Empty(matchmaker.ExpireFindGames(DateTime.UtcNow.AddSeconds(29)));
        Assert.Single(matchmaker.ExpireFindGames(DateTime.UtcNow.AddSeconds(31)));
        Assert.Empty(matchmaker.Queue);
    }
}

/// <summary>A ranked match the way the client walks it: match server, match, back to the lobby, join, relay.</summary>
[Collection(MediusServerCollection.Name)]
public sealed class ResistanceFallOfManRankedFlowTests(LobbyFlowFixture fixture)
{
    private const int Ps3 = 113;
    private const int AppId = LobbyFlowFixture.AppId;
    private static readonly byte[] Extra = new byte[11];

    private sealed record Session(string SessionKey, string AccessKey, int AccountId, int WorldId);

    private async Task<(MediusTestClient Mls, Session Session)> LoginAsync(string name)
    {
        await using MediusTestClient mas = new(Ps3);
        await mas.ConnectAsync(fixture.MasPort);
        await mas.HandshakeAsync(AppId);
        mas.SendApp(new MediusSessionBeginRequest { MessageID = new MessageId("s"), ConnectionClass = MediusConnectionType.Ethernet });
        MediusSessionBeginResponse session = await mas.ExpectAppAsync<MediusSessionBeginResponse>();
        mas.SendApp(new MediusTicketLoginRequest { MessageID = new MessageId("l"), SessionKey = session.SessionKey, Username = name });
        MediusTicketLoginResponse login = await mas.ExpectAppAsync<MediusTicketLoginResponse>();

        MediusTestClient mls = new(Ps3);
        await mls.ConnectAsync(fixture.MlsPort);
        await mls.HandshakeAsync(AppId, session.SessionKey, login.ConnectInfo.AccessKey, (uint)login.MediusWorldID);
        return (mls, new Session(session.SessionKey, login.ConnectInfo.AccessKey, login.AccountID, login.MediusWorldID));
    }

    /// <summary>Asks the MLS for the superset's match server and moves there, as lobby_roomMgr does.</summary>
    private async Task<MediusTestClient> MoveToMatchServerAsync(MediusTestClient mls, Session session, uint superset)
    {
        mls.SendApp(new MediusMatchFindGameRequest { MessageID = new MessageId("r"), SessionKey = session.SessionKey, SupersetID = superset, AccountIDs = [session.AccountId] });
        MediusMatchFindGameResponse referral = await mls.ExpectAppAsync<MediusMatchFindGameResponse>();
        Assert.Equal(MediusCallbackStatus.MediusMatchTypeReferral, referral.StatusCode);
        Assert.Equal((uint)fixture.MlsPort, referral.ConnectInfo.AddressList.AddressList[0].Port);
        Assert.Equal((int)superset, referral.ConnectInfo.WorldID);
        await mls.DisposeAsync();

        MediusTestClient mms = new(Ps3);
        await mms.ConnectAsync(fixture.MlsPort);
        await mms.HandshakeAsync(AppId, referral.ConnectInfo.SessionKey, referral.ConnectInfo.AccessKey, (uint)referral.ConnectInfo.WorldID);
        return mms;
    }

    private static void RequestMatch(MediusTestClient mms, Session session, uint superset, string id) =>
        mms.SendApp(new MediusMatchFindGameRequest { MessageID = new MessageId(id), SessionKey = session.SessionKey, SupersetID = superset, AccountIDs = [session.AccountId], Extra = Extra });

    [Fact]
    public async Task An_unknown_superset_has_no_match_server()
    {
        var (mls, session) = await LoginAsync("rfom-nowhere");
        await using MediusTestClient _ = mls;
        mls.SendApp(new MediusMatchFindGameRequest { MessageID = new MessageId("r"), SessionKey = session.SessionKey, SupersetID = 99, AccountIDs = [session.AccountId] });
        Assert.Equal(MediusCallbackStatus.MediusMatchServerNotFound, (await mls.ExpectAppAsync<MediusMatchFindGameResponse>()).StatusCode);
    }

    [Fact]
    public async Task Two_players_are_matched_go_back_to_the_lobby_join_and_talk()
    {
        var (aliceMls, alice) = await LoginAsync("rfom-alice");
        var (bobMls, bob) = await LoginAsync("rfom-bob");
        await using MediusTestClient aliceMms = await MoveToMatchServerAsync(aliceMls, alice, 2);
        await using MediusTestClient bobMms = await MoveToMatchServerAsync(bobMls, bob, 2);

        // Alice waits unanswered until Bob completes the pool.
        RequestMatch(aliceMms, alice, 2, "a");
        RequestMatch(bobMms, bob, 2, "b");
        MediusMatchFindGameResponse aliceMatch = await aliceMms.ExpectAppAsync<MediusMatchFindGameResponse>();
        MediusMatchFindGameResponse bobMatch = await bobMms.ExpectAppAsync<MediusMatchFindGameResponse>();
        Assert.Equal(MediusCallbackStatus.MediusJoinAssignedGame, aliceMatch.StatusCode);
        Assert.Equal("a", aliceMatch.MessageID.Value);
        Assert.Equal("b", bobMatch.MessageID.Value);
        int worldId = aliceMatch.GameInfo.MediusWorldID;
        Assert.Equal(worldId, bobMatch.GameInfo.MediusWorldID);
        Assert.Equal(new[] { alice.AccountId, bob.AccountId }.Order(), aliceMatch.GameInfo.Teams.SelectMany(t => t.Members).Select(m => m.AccountID).Order());

        // The way back to the lobby.
        aliceMms.SendApp(new MediusJoinLeastPopulatedChannelRequest { MessageID = new MessageId("c"), SessionKey = alice.SessionKey });
        MediusJoinLeastPopulatedChannelResponse channel = await aliceMms.ExpectAppAsync<MediusJoinLeastPopulatedChannelResponse>();
        Assert.Equal(MediusCallbackStatus.MediusSuccess, channel.StatusCode);
        Assert.Equal((uint)fixture.MlsPort, channel.ConnectInfo.AddressList.AddressList[0].Port);
        await aliceMms.DisposeAsync();
        await using MediusTestClient aliceLobby = new(Ps3);
        await aliceLobby.ConnectAsync(fixture.MlsPort);
        await aliceLobby.HandshakeAsync(AppId, channel.ConnectInfo.SessionKey, channel.ConnectInfo.AccessKey, (uint)channel.ConnectInfo.WorldID);

        // Both join the assigned world and reach each other through the DME.
        MediusJoinGameResponse aliceJoin = await JoinAsync(aliceLobby, alice, worldId);
        MediusJoinGameResponse bobJoin = await JoinAsync(bobMms, bob, worldId);
        await using MediusTestClient aliceDme = new(Ps3);
        await aliceDme.ConnectAsync(fixture.DmePort);
        RT_MSG_SERVER_CONNECT_ACCEPT_TCP aliceAccept = await aliceDme.DmeHandshakeAsync(AppId, aliceJoin.ConnectInfo);
        await using MediusTestClient bobDme = new(Ps3);
        await bobDme.ConnectAsync(fixture.DmePort);
        RT_MSG_SERVER_CONNECT_ACCEPT_TCP bobAccept = await bobDme.DmeHandshakeAsync(AppId, bobJoin.ConnectInfo);
        await aliceDme.ExpectAsync<RT_MSG_SERVER_CONNECT_NOTIFY>();

        // The host's answer to a joiner comes as a QoS single.
        aliceDme.Send(new RT_MSG_CLIENT_APP_SINGLE_QOS { QosFlags = 0x50, Target = (short)bobAccept.PlayerId, Payload = [0, 0x10, 1] });
        RT_MSG_CLIENT_APP_SINGLE single = await bobDme.ExpectAsync<RT_MSG_CLIENT_APP_SINGLE>();
        Assert.Equal((short)aliceAccept.PlayerId, single.TargetOrSource);
        Assert.Equal(new byte[] { 0, 0x10, 1 }, single.Payload);

        bobDme.Send(new RT_MSG_CLIENT_APP_LIST_QOS { QosFlags = 0x50, Targets = [aliceAccept.PlayerId], Payload = [7] });
        Assert.Equal(new byte[] { 7 }, (await aliceDme.ExpectAsync<RT_MSG_CLIENT_APP_SINGLE>()).Payload);

        // The lag check's pings come over UDP and must come back with their instance.
        bobDme.SendUdp(new RT_MSG_CLIENT_APP_TOSERVER { Message = new DMETypePing { PingInstance = 5, RequestEcho = 1 } });
        DMETypePing pong = await bobDme.ExpectAppAsync<DMETypePing>();
        Assert.Equal((byte)5, pong.PingInstance);
        Assert.Equal((byte)0, pong.RequestEcho);
        Assert.NotEqual(0u, pong.Time);
    }

    [Fact]
    public async Task A_full_pool_entry_answers_at_once_with_the_pools_options()
    {
        var (mls, session) = await LoginAsync("rfom-solo");
        await using MediusTestClient mms = await MoveToMatchServerAsync(mls, session, 1);
        RequestMatch(mms, session, 1, "m");
        MediusMatchFindGameResponse match = await mms.ExpectAppAsync<MediusMatchFindGameResponse>();
        Assert.Equal(new byte[] { 0x0A, 0x0B }, match.GameInfo.GameOptions);
        Assert.Equal((uint)fixture.DmePort, match.GameInfo.AddressList.AddressList[0].Port);
    }

    private static async Task<MediusJoinGameResponse> JoinAsync(MediusTestClient lobby, Session session, int worldId)
    {
        lobby.SendApp(new MediusJoinGameRequest
        {
            MessageID = new MessageId("j"),
            SessionKey = session.SessionKey,
            MediusWorldID = worldId,
            JoinType = MediusJoinType.MediusJoinAsPlayer,
            GamePassword = "",
            GameHostType = MediusGameHostType.MediusGameHostClientServerAuxUDP,
            pubKey = new RSA_KEY(),
            AddressList = new NetAddressList(),
        });
        MediusJoinGameResponse join = await lobby.ExpectAppAsync<MediusJoinGameResponse>();
        Assert.Equal(MediusCallbackStatus.MediusSuccess, join.StatusCode);
        return join;
    }
}
