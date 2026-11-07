using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;
using System.Buffers.Binary;

namespace Plugin.Medius.Tests;

/// <summary>
/// The SCE-RT matchmaking messages byte for byte against the client's own
/// serializers (Resistance 2 EBOOT; addresses in MediusMatchMessages.cs).
/// </summary>
public sealed class MatchMessageLayoutTests
{
    private static byte[] Write(BaseMediusMessage message)
    {
        using MemoryStream stream = new();
        using (MessageWriter writer = new(stream))
            message.Serialize(writer);
        return stream.ToArray();
    }

    private static T Read<T>(byte[] bytes) where T : BaseMediusMessage, new()
    {
        T message = new();
        using MessageReader reader = new(new MemoryStream(bytes));
        message.Deserialize(reader);
        return message;
    }

    [Fact]
    public void Party_request_is_packed_like_the_client_writes_it()
    {
        // 0x808900: MessageID[21] SessionKey[17] [16] then seven u32 (superset,
        // count, 2, timeout, 0, 3, attributes length), the ids, the attributes.
        byte[] bytes = new byte[21 + 17 + 16 + 7 * 4 + 2 * 4 + 3];
        "m1"u8.CopyTo(bytes);
        "12345"u8.CopyTo(bytes.AsSpan(21));
        Span<byte> u32s = bytes.AsSpan(54);
        uint[] values = [2, 2, 2, 60, 0, 3, 3, 1001, 1002];
        for (int i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(u32s[(4 * i)..], values[i]);
        bytes[^3] = 0xAA;
        bytes[^1] = 0xCC;

        MediusMatchPartyRequest request = Read<MediusMatchPartyRequest>(bytes);
        Assert.Equal("m1", request.MessageID.Value);
        Assert.Equal("12345", request.SessionKey);
        Assert.Equal(2u, request.SupersetID);
        Assert.Equal((2u, 60u, 0u, 3u), (request.Unk40, request.Unk44, request.Unk48, request.Unk4C));
        Assert.Equal(new uint[] { 1001, 1002 }, request.PlayerAccountIDs);
        Assert.Equal(new byte[] { 0xAA, 0, 0xCC }, request.Attributes);
        Assert.Equal(bytes, Write(request));
    }

    [Fact]
    public void Status_responses_pad_three_bytes_before_the_status()
    {
        byte[] bytes = Write(new MediusMatchPartyResponse { MessageID = new MessageId("x"), StatusCode = MediusCallbackStatus.MediusMatchServerNotFound });
        Assert.Equal(28, bytes.Length);
        Assert.Equal(-928, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24)));
    }

    [Fact]
    public void Superset_response_has_no_pad_and_unterminated_strings()
    {
        byte[] bytes = Write(new MediusMatchListSupersetsResponse
        {
            MessageID = new MessageId("s"),
            StatusCode = MediusCallbackStatus.MediusSuccess,
            EndOfList = true,
            SupersetID = 2,
            SupersetName = "Versus",
        });
        // MessageID[21], status u32, EndOfList u8, id, Unk24, three lengths, "Versus".
        Assert.Equal(21 + 4 + 1 + 5 * 4 + 6, bytes.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(21)));
        Assert.Equal(1, bytes[25]);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(26)));
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(34)));
        Assert.Equal("Versus"u8.ToArray(), bytes[^6..]);
        Assert.Equal("Versus", Read<MediusMatchListSupersetsResponse>(bytes).SupersetName);
    }

    [Fact]
    public void Match_found_sizes_are_the_clients_decoded_sizes()
    {
        MediusMatchFoundResponse found = new()
        {
            MessageID = new MessageId("p"),
            StatusCode = MediusCallbackStatus.MediusJoinAssignedGame,
            GameInfo = new MediusMatchGameInfo
            {
                MediusWorldID = 7,
                GameOptions = [1, 2, 3],
                Teams =
                [
                    new MediusMatchTeam { Members = [new() { AccountID = 11 }, new() { AccountID = 12 }] },
                    new MediusMatchTeam { Members = [new() { AccountID = 13 }] },
                ],
            },
        };
        byte[] bytes = Write(found);

        // Teams decode to 8 + 8n (0x81a310); the roster to 8 + 4 per team + teams (0x81a330);
        // the game info to 100 + options + roster (0x81a3b8).
        Assert.Equal(24, found.GameInfo.Teams[0].DecodedSize);
        Assert.Equal(8 + 4 * 2 + 24 + 16, found.GameInfo.RosterDecodedSize);
        Assert.Equal(100 + 3 + 56, found.GameInfo.DecodedSize);

        int header = 21 + 16 + 4 * 4;
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(21 + 16 + 8)));
        Assert.Equal(159, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(header)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(header + 4)));
        // world, [32], u32, two 24-byte addresses, options length + options, roster size, team count,
        // per team its size and members (count, then id and flag each).
        int expected = header + 4 + 4 + 32 + 4 + 48 + 4 + 3 + 4 + 4 + (4 + 4 + 16) + (4 + 4 + 8);
        Assert.Equal(expected, bytes.Length);

        MediusMatchFoundResponse back = Read<MediusMatchFoundResponse>(bytes);
        Assert.Equal(7, back.GameInfo.MediusWorldID);
        Assert.Equal(new[] { 11, 12 }, back.GameInfo.Teams[0].Members.Select(m => m.AccountID));
        Assert.Equal(13, Assert.Single(back.GameInfo.Teams[1].Members).AccountID);
    }

    [Fact]
    public void No_match_carries_no_game_info()
    {
        byte[] bytes = Write(new MediusMatchFoundResponse { MessageID = new MessageId("p"), StatusCode = MediusCallbackStatus.MediusMatchGameCreationFailed });
        Assert.Equal(21 + 16 + 16, bytes.Length);
    }
}

public sealed class MatchmakerTests
{
    [Fact]
    public void Pool_key_is_the_configured_attribute_bytes()
    {
        MediusMatchmakingSettings settings = new() { PoolKey = [new() { Offset = 6, Size = 2 }] };
        Assert.Equal("0005", settings.KeyOf([0, 1, 0, 0, 9, 9, 0, 5, 7]));
        Assert.Equal("----", settings.KeyOf([0, 1]));
    }

    [Fact]
    public void Teams_keep_parties_together_and_balance_sizes()
    {
        MediusTitle title = new();
        MediusMatchEntry Party(int size, int order) => new(new MediusClient(title, 1, 113), new MessageId(), 2, "",
            Enumerable.Range(order * 10, size).ToList(), [], DateTime.UnixEpoch.AddSeconds(order));

        var teams = MediusMatchmaker.AssignTeams([Party(1, 0), Party(3, 1), Party(2, 2), Party(1, 3)], 2);
        Assert.Equal(new[] { 4, 3 }, Enumerable.Range(0, 2).Select(t => teams.Where(x => x.Team == t).Sum(x => x.Party.Size)));
        Assert.Equal(0, teams.Single(x => x.Party.Size == 3).Team);

        // A late party goes onto the team the game is short of.
        Assert.Equal(1, Assert.Single(MediusMatchmaker.AssignTeams([Party(1, 4)], 2, [1, 0])).Team);
    }

    [Fact]
    public void Roster_per_party_lists_the_party_tagged_with_its_team()
    {
        MediusTitle title = new();
        MediusMatchEntry first = new(new MediusClient(title, 1, 113), new MessageId(), 2, "", [95589], [], DateTime.UnixEpoch);
        MediusMatchEntry second = new(new MediusClient(title, 1, 113), new MessageId(), 2, "", [95650], [], DateTime.UnixEpoch);
        MediusMatchPool pool = new() { Teams = 2 };
        MediusMatch match = new(null!, pool, [(first, 0), (second, 1)]);

        // PSRewired's two MatchFoundResponses for one game: "95589/0" and "95650/1" [live R2 1.60].
        MediusMatchmakingSettings perParty = new() { RosterPerParty = true };
        Assert.Equal("95589/0", Assert.Single(match.RosterFor(first, perParty)).ToString());
        Assert.Equal("95650/1", Assert.Single(match.RosterFor(second, perParty)).ToString());
        Assert.Equal(["95589/0", "95650/0"], match.RosterFor(second, new MediusMatchmakingSettings()).Select(t => t.ToString()));
    }
}

/// <summary>Matchmaking end to end on the MLS: supersets, queueing, a match, and joining the matched world.</summary>
[Collection(MediusServerCollection.Name)]
public sealed class MatchmakingFlowTests(LobbyFlowFixture fixture)
{
    private const int Ps3 = 113;

    private static byte[] Attributes(ushort gameType) => [0, 1, 0, 0, 0, 0, (byte)(gameType >> 8), (byte)gameType, 0, 0];

    private async Task<(MediusTestClient Mls, string SessionKey, int AccountId)> LoginAsync(string name)
    {
        await using MediusTestClient mas = new(Ps3);
        await mas.ConnectAsync(fixture.MasPort);
        await mas.HandshakeAsync(LobbyFlowFixture.AppId);
        mas.SendApp(new MediusSessionBeginRequest { MessageID = new MessageId("s"), ConnectionClass = MediusConnectionType.Ethernet });
        MediusSessionBeginResponse session = await mas.ExpectAppAsync<MediusSessionBeginResponse>();
        mas.SendApp(new MediusTicketLoginRequest { MessageID = new MessageId("l"), SessionKey = session.SessionKey, Username = name });
        MediusTicketLoginResponse login = await mas.ExpectAppAsync<MediusTicketLoginResponse>();
        Assert.Equal(MediusCallbackStatus.MediusSuccess, login.StatusCode);

        MediusTestClient mls = new(Ps3);
        await mls.ConnectAsync(fixture.MlsPort);
        await mls.HandshakeAsync(LobbyFlowFixture.AppId, session.SessionKey, login.ConnectInfo.AccessKey, (uint)login.MediusWorldID);
        return (mls, session.SessionKey, login.AccountID);
    }

    [Fact]
    public async Task Supersets_are_listed_in_order()
    {
        var (mls, key, _) = await LoginAsync("superset-viewer");
        await using MediusTestClient _ = mls;
        mls.SendApp(new MediusMatchListSupersetsRequest { MessageID = new MessageId("ss"), SessionKey = key });
        MediusMatchListSupersetsResponse first = await mls.ExpectAppAsync<MediusMatchListSupersetsResponse>();
        MediusMatchListSupersetsResponse second = await mls.ExpectAppAsync<MediusMatchListSupersetsResponse>();
        Assert.Equal((1u, "Co-op", false), (first.SupersetID, first.SupersetName, first.EndOfList));
        Assert.Equal((2u, "Versus", "two teams", true), (second.SupersetID, second.SupersetName, second.SupersetDescription, second.EndOfList));
    }

    [Fact]
    public async Task Two_parties_are_matched_onto_teams_and_join_the_world()
    {
        var (aliceMls, aliceKey, aliceId) = await LoginAsync("alice-match");
        var (bobMls, bobKey, bobId) = await LoginAsync("bob-match");
        await using MediusTestClient a = aliceMls, b = bobMls;

        // Alice waits alone: the pool needs two.
        aliceMls.SendApp(new MediusMatchPartyRequest { MessageID = new MessageId("pa"), SessionKey = aliceKey, SupersetID = 2, PlayerAccountIDs = [(uint)aliceId], Attributes = Attributes(2) });
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await aliceMls.ExpectAppAsync<MediusMatchPartyResponse>()).StatusCode);

        // Bob in another game type does not complete her pool; in hers he does.
        bobMls.SendApp(new MediusMatchPartyRequest { MessageID = new MessageId("pb"), SessionKey = bobKey, SupersetID = 2, PlayerAccountIDs = [(uint)bobId], Attributes = Attributes(3) });
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await bobMls.ExpectAppAsync<MediusMatchPartyResponse>()).StatusCode);
        bobMls.SendApp(new MediusMatchPartyRequest { MessageID = new MessageId("pc"), SessionKey = bobKey, SupersetID = 2, PlayerAccountIDs = [(uint)bobId], Attributes = Attributes(2) });
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await bobMls.ExpectAppAsync<MediusMatchPartyResponse>()).StatusCode);

        MediusMatchFoundResponse aliceFound = await aliceMls.ExpectAppAsync<MediusMatchFoundResponse>();
        MediusMatchFoundResponse bobFound = await bobMls.ExpectAppAsync<MediusMatchFoundResponse>();
        Assert.Equal(MediusCallbackStatus.MediusJoinAssignedGame, aliceFound.StatusCode);
        Assert.Equal("pa", aliceFound.MessageID.Value);
        Assert.Equal("pc", bobFound.MessageID.Value);
        int worldId = aliceFound.GameInfo.MediusWorldID;
        Assert.Equal(worldId, bobFound.GameInfo.MediusWorldID);
        Assert.Equal((uint)fixture.DmePort, aliceFound.GameInfo.AddressList.AddressList[0].Port);
        Assert.Equal(2, aliceFound.GameInfo.Teams.Count);
        Assert.Equal(new[] { aliceId, bobId }.Order(), aliceFound.GameInfo.Teams.SelectMany(t => t.Members).Select(m => m.AccountID).Order());

        aliceMls.SendApp(new MediusJoinGameRequest
        {
            MessageID = new MessageId("j"),
            SessionKey = aliceKey,
            MediusWorldID = worldId,
            JoinType = MediusJoinType.MediusJoinAsPlayer,
            GamePassword = "",
            GameHostType = MediusGameHostType.MediusGameHostClientServerAuxUDP,
            pubKey = new RSA_KEY(),
            AddressList = new NetAddressList(),
        });
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await aliceMls.ExpectAppAsync<MediusJoinGameResponse>()).StatusCode);
    }

    [Fact]
    public async Task A_single_player_pool_matches_at_once_with_its_options()
    {
        var (mls, key, id) = await LoginAsync("solo-coop");
        await using MediusTestClient _ = mls;
        mls.SendApp(new MediusMatchPartyRequest { MessageID = new MessageId("co"), SessionKey = key, SupersetID = 1, PlayerAccountIDs = [(uint)id], Attributes = Attributes(5) });
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await mls.ExpectAppAsync<MediusMatchPartyResponse>()).StatusCode);
        MediusMatchFoundResponse found = await mls.ExpectAppAsync<MediusMatchFoundResponse>();
        Assert.Equal(new byte[] { 0x0A, 0x0B }, found.GameInfo.GameOptions);
        Assert.Equal(id, Assert.Single(Assert.Single(found.GameInfo.Teams).Members).AccountID);
    }

    [Fact]
    public async Task Later_parties_fill_the_pools_open_game_until_late_join_closes()
    {
        var (aMls, aKey, aId) = await LoginAsync("backfill-a");
        var (bMls, bKey, bId) = await LoginAsync("backfill-b");
        var (cMls, cKey, cId) = await LoginAsync("backfill-c");
        await using MediusTestClient a = aMls, b = bMls, c = cMls;

        async Task<MediusMatchFoundResponse> QueueAsync(MediusTestClient mls, string key, int id)
        {
            mls.SendApp(new MediusMatchPartyRequest { MessageID = new MessageId("q"), SessionKey = key, SupersetID = 1, PlayerAccountIDs = [(uint)id], Attributes = Attributes(7) });
            Assert.Equal(MediusCallbackStatus.MediusSuccess, (await mls.ExpectAppAsync<MediusMatchPartyResponse>()).StatusCode);
            return await mls.ExpectAppAsync<MediusMatchFoundResponse>();
        }

        // A one-player pool makes a game for the first party at once...
        int world = (await QueueAsync(aMls, aKey, aId)).GameInfo.MediusWorldID;
        aMls.SendApp(new MediusJoinGameRequest
        {
            MessageID = new MessageId("j"),
            SessionKey = aKey,
            MediusWorldID = world,
            JoinType = MediusJoinType.MediusJoinAsPlayer,
            GamePassword = "",
            GameHostType = MediusGameHostType.MediusGameHostClientServerAuxUDP,
            pubKey = new RSA_KEY(),
            AddressList = new NetAddressList(),
        });
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await aMls.ExpectAppAsync<MediusJoinGameResponse>()).StatusCode);

        // ...and the next party is sent to it rather than to a game of its own.
        MediusMatchFoundResponse bFound = await QueueAsync(bMls, bKey, bId);
        Assert.Equal(world, bFound.GameInfo.MediusWorldID);
        Assert.Equal(bId, Assert.Single(Assert.Single(bFound.GameInfo.Teams).Members).AccountID);

        // Once the game closes late join, the next party gets a new game.
        aMls.SendApp(new MediusMatchCloseLateJoinRequest { MessageID = new MessageId("cl"), SessionKey = aKey });
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await aMls.ExpectAppAsync<MediusMatchCloseLateJoinResponse>()).StatusCode);
        Assert.NotEqual(world, (await QueueAsync(cMls, cKey, cId)).GameInfo.MediusWorldID);
    }

    [Fact]
    public async Task Unknown_superset_is_refused()
    {
        var (mls, key, id) = await LoginAsync("lost-party");
        await using MediusTestClient _ = mls;
        mls.SendApp(new MediusMatchPartyRequest { MessageID = new MessageId("x"), SessionKey = key, SupersetID = 9, PlayerAccountIDs = [(uint)id], Attributes = Attributes(1) });
        Assert.Equal(MediusCallbackStatus.MediusMatchServerNotFound, (await mls.ExpectAppAsync<MediusMatchPartyResponse>()).StatusCode);
    }
}

public sealed class MatchBlobTests
{
    [Fact]
    public void Options_blob_is_built_from_hex_fields_and_text()
    {
        MediusTitle title = new();
        MediusMatchPool pool = new()
        {
            GameOptionsParts =
            [
                new() { Hex = "0000" },
                new() { Field = "MatchId", Encoding = "u32be" },
                new() { Lines = ["Global,,", ",GameType,CTF"], NulTerminated = true },
            ],
        };
        MediusGame game = new(0x01020304, title, pool, new MediusClient(title, 1, 113));
        byte[] blob = new MediusMatch(game, pool, []).GameOptions();
        Assert.Equal(new byte[] { 0, 0, 1, 2, 3, 4 }, blob[..6]);
        Assert.Equal("Global,,\r\n,GameType,CTF\0"u8.ToArray(), blob[6..]);
    }

    [Fact]
    public void A_game_is_one_of_its_pools_variants_with_the_rest_inherited()
    {
        MediusMatchPool pool = new()
        {
            Name = "Any",
            MinPlayers = 1,
            MaxPlayers = 8,
            Teams = 2,
            HostType = MediusGameHostType.MediusGameHostType5,
            Values = { ["Mode"] = "x", ["Map"] = "none" },
            GameOptionsParts = [new() { Lines = ["{Name}", "{Mode}", "{Map}", "{MaxPlayers}", "{Teams}"], LineEnding = "|" }],
            Variants =
            [
                new() { Name = "Small", MaxPlayers = 10, Teams = 1, Values = { ["Map"] = "a" } },
                new() { Values = { ["Map"] = "b" }, Weight = 0 },
                new() { Values = { ["Map"] = "c" } },
            ],
        };
        HashSet<string> seen = [];
        Random random = new(7);
        for (int i = 0; i < 200; i++)
        {
            MediusMatchPool game = pool.ForGame(random);
            Assert.Equal((1, MediusGameHostType.MediusGameHostType5), (game.MinPlayers, game.HostType));
            seen.Add(System.Text.Encoding.UTF8.GetString(game.GameOptionsParts[0].Build(new MediusMatch(null!, game, []))));
        }
        // Weight 0 is never picked; unset fields are the pool's.
        Assert.Equal(["Any|x|c|8|2", "Small|x|a|10|1"], seen.Order());
        MediusMatchPool plain = new();
        Assert.Same(plain, plain.ForGame(random));
    }
}
