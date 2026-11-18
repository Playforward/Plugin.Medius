using Plugin.Medius.Extensions.Killzone3;
using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;
using Plugin.Medius.Web;
using ServerShared.Titles;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Plugin.Medius.Tests;

/// <summary>
/// Killzone 3 (1.14): the SCE-RT 3.06 layouts read from its registration table, its
/// title file, and the login service of Extensions/Killzone3. All static evidence
/// (RE-Work/kz3/NOTES.md); nothing here has met the live client yet.
/// </summary>
public sealed class Killzone3Tests
{
    // Killzone 3 1.14's rt_msg_client ReleaseVersion.
    private const string RtVersion = "3.06.201103140900";
    private const int MediusVersion = 113;

    private static ScertLayout Layout => new MediusTitle { RtVersion = RtVersion }.RtLayout;

    private static byte[] AppBytes(BaseMediusMessage message)
    {
        using MemoryStream stream = new();
        using (MessageWriter writer = new(stream) { MediusVersion = MediusVersion })
            new RT_MSG_SERVER_APP { Message = message }.Serialize(writer);
        return stream.ToArray();
    }

    [Fact]
    public void Version_selects_the_306_profile()
    {
        Assert.True(ScertLibraryProfile.TryFind(RtVersion, out ScertLibraryProfile profile));
        Assert.Same(ScertLibraryProfile.Rt306, profile);
        Assert.Equal(new ScertLayout
        {
            UniverseExtendedInfoLength = 256,
            UniversePadAfterEndOfList = false,
            JoinGameTrailingPadLength = 4,
            DmeServerVersionLength = 20,
            SvoUrlFieldLength = 128,
            BinaryFwdOriginatorFirst = true,
            SupersetFixedStrings = true,
            AnnouncementLength = 4000,
            ClanExtraInfoLadderFields = false,
            MatchMessages306 = true,
        }, Layout);
    }

    [Fact]
    public void Older_profiles_keep_their_superset_announcement_and_clan_layouts()
    {
        foreach (ScertLibraryProfile profile in new[] { ScertLibraryProfile.Default, ScertLibraryProfile.Rt303, ScertLibraryProfile.Rt398, ScertLibraryProfile.Rt302 })
        {
            Assert.False(profile.Layout.SupersetFixedStrings);
            Assert.Equal(Constants.ANNOUNCEMENT_MAXLEN, profile.Layout.AnnouncementLength);
            Assert.True(profile.Layout.ClanExtraInfoLadderFields);
        }
    }

    /// <summary>Codec 0xa290d0: a u32 at +0xbc after ConnectInfo, struct 0xc0.</summary>
    [Fact]
    public void Join_game_response_is_0xC0_bytes()
    {
        byte[] bytes = AppBytes(new MediusJoinGameResponse
        {
            MessageID = new MessageId("1"),
            ConnectInfo = new NetConnectionInfo(),
            TrailingPadLength = Layout.JoinGameTrailingPadLength,
        });
        Assert.Equal(0xC0, bytes.Length - 2);
    }

    /// <summary>Codec 0xa29294: ConnectInfo, the new u32, then 3.03's last u32 (struct 0xc4).</summary>
    [Fact]
    public void Join_game2_response_has_a_u32_between_ConnectInfo_and_its_last_field()
    {
        MediusJoinGameResponse2 response = new()
        {
            MessageID = new MessageId("1"),
            ConnectInfo = new NetConnectionInfo(),
            Unknown_BC = 0x11223344,
            ConnectInfoPadLength = Layout.JoinGameTrailingPadLength ?? 0,
        };
        byte[] bytes = AppBytes(response);
        Assert.Equal(0xC4, bytes.Length - 2);
        Assert.Equal([0x44, 0x33, 0x22, 0x11], bytes[^4..]);
        Assert.Equal([0, 0, 0, 0], bytes[^8..^4]);
    }

    /// <summary>Codec 0xa29008: MessageID, StatusCode, EndOfList, SupersetID, name[50], description[256], extra[256].</summary>
    [Fact]
    public void Supersets_carry_fixed_width_strings_and_round_trip()
    {
        MediusMatchListSupersetsResponse response = new()
        {
            MessageID = new MessageId("7"),
            StatusCode = MediusCallbackStatus.MediusSuccess,
            EndOfList = true,
            SupersetID = 3,
            SupersetName = "WARZONE",
            SupersetDescription = "Team modes",
            SupersetExtraInfo = "x",
            FixedStrings = Layout.SupersetFixedStrings,
        };
        byte[] bytes = AppBytes(response);
        Assert.Equal(21 + 4 + 1 + 4 + 50 + 256 + 256, bytes.Length - 2);
        Assert.Equal("WARZONE", Encoding.ASCII.GetString(bytes, 2 + 30, 7));

        MediusMatchListSupersetsResponse read = new() { FixedStrings = true };
        using (MessageReader reader = new(new MemoryStream(bytes, 2, bytes.Length - 2)) { MediusVersion = MediusVersion })
            read.Deserialize(reader);
        Assert.Equal(3u, read.SupersetID);
        Assert.Equal("WARZONE", read.SupersetName);
        Assert.Equal("Team modes", read.SupersetDescription);
        Assert.Equal("x", read.SupersetExtraInfo);
        Assert.True(read.EndOfList);
    }

    /// <summary>Codec 0xa34950: `li r5,0xfa0` for the text.</summary>
    [Fact]
    public void Announcements_carry_4000_bytes_of_text()
    {
        byte[] bytes = AppBytes(new MediusGetAnnouncementsResponse
        {
            MessageID = new MessageId("1"),
            Announcement = "hello",
            EndOfList = true,
            AnnouncementLength = Layout.AnnouncementLength,
        });
        Assert.Equal(21 + 3 + 4 + 4 + 4000 + 1 + 3, bytes.Length - 2);
    }

    /// <summary>Codec 0xa34424: no LadderStat/LadderPosition/TotalRankings (struct 0x1dc -> 0x1d0).</summary>
    [Fact]
    public void Clan_member_extra_info_drops_the_three_ladder_fields()
    {
        MediusGetClanMemberList_ExtraInfoResponse Member(bool ladder) => new()
        {
            MessageID = new MessageId("1"),
            AccountName = "a",
            Stats = new byte[Constants.ACCOUNTSTATS_MAXLEN],
            OnlineState = new MediusPlayerOnlineState(),
            EndOfList = true,
            LadderFields = ladder,
        };
        Assert.Equal(12, AppBytes(Member(true)).Length - AppBytes(Member(Layout.ClanExtraInfoLadderFields)).Length);
    }

    /// <summary>
    /// Codec 0xa288e4. Requests are decoded before their title is known, so the 3.03 read runs
    /// first; ReadAs306 re-reads the kept body.
    /// </summary>
    [Fact]
    public void Match_party_body_is_re_read_under_the_306_layout()
    {
        MediusMatchPartyRequest sent = new()
        {
            MessageID = new MessageId("16"),
            SessionKey = "13301",
            Layout306 = true,
            SupersetID = 2,
            Unk2C = 7,
            Unk30 = "abc",
            Unk60 = 9,
            Unk75 = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray(),
            PlayerAccountIDs = [10, 11],
            Attributes = [0xAA, 0xBB, 0xCC],
        };
        using MemoryStream stream = new();
        using (MessageWriter writer = new(stream) { MediusVersion = MediusVersion })
            sent.Serialize(writer);
        byte[] body = stream.ToArray();
        // MessageID, SessionKey, 2 u32, char[32], 3 u32, char[4], u32, char[17], char[16], count, length.
        Assert.Equal(21 + 17 + 8 + 32 + 12 + 4 + 4 + 17 + 16 + 8 + 8 + 3, body.Length);

        MediusMatchPartyRequest read = new();
        using (MessageReader reader = new(new MemoryStream(body)) { MediusVersion = MediusVersion })
            read.Deserialize(reader);
        read.ReadAs306();
        Assert.Equal(2u, read.SupersetID);
        Assert.Equal("abc", read.Unk30);
        Assert.Equal(9u, read.Unk60);
        Assert.Equal(sent.Unk75, read.Unk75);
        Assert.Equal([10u, 11u], read.PlayerAccountIDs);
        Assert.Equal([0xAA, 0xBB, 0xCC], read.Attributes);
    }

    /// <summary>Codecs 0xa29d78 and 0xa295f8: no MessageID, a game-world game info without a roster.</summary>
    [Fact]
    public void Match_found_under_306_has_no_message_id_and_a_game_world_info()
    {
        MediusMatchFoundResponse response = new()
        {
            MessageID = new MessageId("16"),
            StatusCode = MediusCallbackStatus.MediusJoinAssignedGame,
            Layout306 = true,
            GameInfo = new MediusMatchGameInfo
            {
                MediusWorldID = 5,
                MatchName = "KZ3-0123456789",
                AddressList = new NetAddressList(),
                GameOptions = [1, 2],
                GameFields = new uint[15],
            },
        };
        byte[] bytes = AppBytes(response);
        int gameInfo = 4 * 3 + 64 + 256 + 15 * 4 + 32 + 4 + 4 + 48 + 4 + 2;
        Assert.Equal(16 + 4 * 4 + 4 + gameInfo, bytes.Length - 2);
        // Status at +0x18 of the body, then the client's decoded size: the 0x1e8-byte struct and the options.
        Assert.Equal((int)MediusCallbackStatus.MediusJoinAssignedGame, BitConverter.ToInt32(bytes, 2 + 0x18));
        Assert.Equal(0x1e8 + 2, BitConverter.ToInt32(bytes, 2 + 0x20));
        Assert.Equal(5, BitConverter.ToInt32(bytes, 2 + 0x24));

        MediusMatchFoundResponse read = new() { Layout306 = true };
        using (MessageReader reader = new(new MemoryStream(bytes, 2, bytes.Length - 2)) { MediusVersion = MediusVersion })
            read.Deserialize(reader);
        Assert.Equal(5, read.GameInfo.MediusWorldID);
        Assert.Equal("KZ3-0123456789", read.GameInfo.MatchName);
        Assert.Equal([1, 2], read.GameInfo.GameOptions);
    }

    // A 1.14 MatchParty's attributes for game type 1: 32 bytes, the last a length (0xb8), then that
    // many base64 characters (an 0x88-byte game settings record) and a NUL [live 2026-11-08].
    private const string LiveMatchAttributes =
        "0103BC000000FFFFFFFF500053080806010A000000DC050000010100300C01B8414C7A2F414141414141442F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F" +
        "2F2F2F2F2F2F3842414141414141414141494146444D414141502F2F2F2F2F2F41414241414141414147514641414141414141414141414141414141" +
        "41414141414141412F7741414141427541484141636742764148482F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F2F41414141" +
        "454159424141414141514141414141414141414341414141415145414141414141413D3D00";

    /// <summary>
    /// The match result's options (0x7f470c: refused when empty, then read as two C strings,
    /// the second base64 game settings for 0x4dab1c) come from the title's parts: the world's
    /// name, then the first party's settings string.
    /// </summary>
    [Fact]
    public void Match_options_are_the_game_name_and_the_partys_settings_string()
    {
        MediusTitle title = new();
        MediusMatchPool pool = new()
        {
            GameOptionsParts =
            [
                new() { Lines = ["{GameName}"], NulTerminated = true },
                new() { AttributeString = 31, NulTerminated = true },
            ],
        };
        byte[] attributes = Convert.FromHexString(LiveMatchAttributes);
        MediusClient client = new(title, 1, 113);
        MediusGame game = new(5, title, pool, client, "KZ3-0123456789");
        MediusMatchEntry party = new(client, new MessageId("16"), 1, "", [], attributes, DateTime.UtcNow);
        byte[] options = new MediusMatch(game, pool, [(party, 0)]).GameOptions();

        string[] strings = Encoding.ASCII.GetString(options).Split('\0');
        Assert.Equal(3, strings.Length);
        Assert.Equal("KZ3-0123456789", strings[0]);
        Assert.Equal(0xb8, strings[1].Length);
        Assert.Equal(0x88, Convert.FromBase64String(strings[1]).Length);
        Assert.Equal("", strings[2]);
    }

    /// <summary>
    /// MatchFound's leading 16 bytes answer the party's request: its counter (attribute byte 0,
    /// put there by 0x165724) and its key (attributes 6-9, written by 0x7f2fc4), which callback
    /// 0x16608c compares before taking the match [C].
    /// </summary>
    [Fact]
    public void Match_found_key_repeats_the_partys_request_counter_and_key()
    {
        MediusMatchmakingSettings settings = new()
        {
            FoundKey = [new() { Offset = 0, Size = 1 }, new() { Offset = 6, Size = 4 }],
        };
        byte[] key = settings.FoundKeyOf(Convert.FromHexString(LiveMatchAttributes));
        Assert.Equal("01FFFFFFFF0000000000000000000000", Convert.ToHexString(key));
        Assert.Equal(new byte[16], new MediusMatchmakingSettings().FoundKeyOf([1, 2, 3]));

        MediusMatchFoundResponse found = new()
        {
            Layout306 = true,
            Unk15 = key,
            StatusCode = MediusCallbackStatus.MediusJoinAssignedGame,
            GameInfo = new MediusMatchGameInfo { MatchName = "KZ3-0123456789", AddressList = new NetAddressList(), GameFields = new uint[15] },
        };
        // After the 2-byte class/type header, the body starts with the key (no MessageID in 3.06).
        Assert.Equal(key, AppBytes(found).AsSpan(2, 16).ToArray());
    }

    private static BaseMediusMessage ReadClient(byte type, string contents)
    {
        byte[] bytes = [(byte)NetMessageTypes.MessageClassLobbyExt, type, .. Convert.FromHexString(contents.Replace("-", ""))];
        using MessageReader reader = new(new MemoryStream(bytes)) { MediusVersion = MediusVersion };
        return BaseMediusMessage.Instantiate(reader, true);
    }

    [Fact]
    public void Match_update_reads_the_hosts_request_and_answers_in_28_bytes()
    {
        // The host's lobby-ext 0x96, seven seconds after the Deck joined its match [live 2026-11-08].
        var request = Assert.IsType<MediusMatchUpdateRequest>(ReadClient(0x96,
            "32-38-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-31-34-31-37-37-00-00-00-00-00-00-00-00-00-00-00-00-01-00-00-00-00-00-00-00"));
        Assert.Equal("28", request.MessageID.ToString());
        Assert.Equal("14177", request.SessionKey);
        Assert.Equal(1u, request.Unk28);
        Assert.Equal(0u, request.Unk2C);
        Assert.Empty(request.Rest);

        byte[] answer = AppBytes(new MediusMatchUpdateResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
        Assert.Equal(2 + 0x1c, answer.Length);
        Assert.Equal(0x97, answer[1]);
    }

    [Fact]
    public void Post_login_query_reads_a_message_id_and_answers_in_32_bytes()
    {
        var request = Assert.IsType<MediusPostLoginQueryRequest>(ReadClient(0x41,
            "31-31-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00-00"));
        Assert.Equal("11", request.MessageID.ToString());

        byte[] answer = AppBytes(new MediusPostLoginQueryResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
        Assert.Equal(2 + 0x20, answer.Length);
        Assert.Equal(0x42, answer[1]);
    }

    /// <summary>A few of the title's stat slots (ScoreProcessor's MPStats columns).</summary>
    private static Killzone3StatsSettings StatsSettings() => new()
    {
        Slots =
        [
            new() { Column = 2, Id = "GamePoints", Cached = true },
            new() { Column = 4, Id = "GamesPlayed" },
            new() { Column = 6, Id = "GameLosses" },
            new() { Column = 7, Id = "GameDraws" },
            new() { Column = 8, Id = "Kills", Cached = true },
            new() { Column = 9, Id = "Deaths", Cached = true },
            new() { Column = 12, Id = "HighKillSpree", Cached = true },
            new() { Column = 13, Id = "TimePlayed", Cached = true },
            new() { Column = 24, Id = "wkills", Type = "Pistol", Cached = true },
            new() { Column = 121, Id = "TimeStarted", Custom = true },
        ],
    };

    /// <summary>finishgame's stats list with <paramref name="values"/> at their columns (133 columns, as 1.14 sends).</summary>
    private static string StatsList(params (int Column, int Value)[] values) => StatsCsv(values).Replace(",", "%2C");

    private static string StatsCsv(params (int Column, int Value)[] values)
    {
        int[] list = new int[133];
        foreach ((int column, int value) in values)
            list[column] = value;
        return string.Join(",", list);
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "kz3-stats-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Stats_store_sums_reports_by_column_keeps_the_best_spree_and_decodes_game_modes()
    {
        string dir = TempDir();
        try
        {
            Killzone3Stats stats = new(new AccountDataStore(dir), "killzone3", StatsSettings());
            stats.Add(7, 23, StatsCsv((2, 120), (4, 1), (7, 1), (8, 5), (9, 3), (12, 4), (13, 931), (121, 99)));
            Killzone3Career career = stats.Add(7, 19, StatsCsv((2, 80), (4, 1), (6, 1), (8, 2), (9, 6), (12, 3), (13, 183)));
            Dictionary<string, long> all = stats.Totals(career);
            Assert.Equal(200, all["GamePoints"]);
            Assert.Equal(2, all["GamesPlayed"]);
            Assert.Equal(7, all["Kills"]);
            Assert.Equal(9, all["Deaths"]);
            Assert.Equal(4, all["HighKillSpree"]);
            Assert.Equal(1114, all["TimePlayed"]);
            Assert.False(all.ContainsKey("TimeStarted"));
            // gameMode = 1 + 2 * (4 * region + type) (0x51db40): 23 is region 2 Skirmish, 19 region 2 Warzone [live].
            Assert.Equal((3, 2), stats.Decode(23));
            Assert.Equal((1, 2), stats.Decode(19));
            Assert.Equal((0, 0), stats.Decode(1));
            Assert.Equal(120, stats.Totals(career, (3, 0))["GamePoints"]);
            Assert.Equal(200, stats.Totals(career, (0, 0))["GamePoints"]);
            Assert.Empty(stats.Totals(career, (2, 0)));
            Assert.Equal([7], stats.Players());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Finish_game_keeps_the_report_and_the_stats_urns_and_login_answer_from_it()
    {
        string dir = TempDir();
        try
        {
            MediusAccount pc = new() { AccountId = 10, Name = "dyllan501" }, deck = new() { AccountId = 11, Name = "dyllan500" };
            Killzone3Extension extension = Extension(pc, deck);
            extension.NameOf = id => new[] { pc, deck }.FirstOrDefault(a => a.AccountId == id)?.Name;
            extension.Stats = new Killzone3Stats(new AccountDataStore(dir), "killzone3", StatsSettings());

            string Session(MediusAccount account)
            {
                XElement login = LoginData(extension.HandleWeb(Get($"/killzone3/kin/account/nplogin.do?id={account.Name}"))!);
                string uri = login.Element("urnstore")!.Elements("urn").First().Element("uri")!.Value;
                return new Uri(uri).AbsolutePath[..(new Uri(uri).AbsolutePath.IndexOf("/kin/", StringComparison.Ordinal) + 1)];
            }
            string pcBase = Session(pc), deckBase = Session(deck);
            // The live 1.14 report of a lost Warzone match (the stats list in the query, no body).
            Assert.Equal("OK", Data(extension.HandleWeb(Get($"{pcBase}kin/game/finish?SVOGameID=1&endStatus=2&gameMode=19&stats=" +
                StatsList((2, 150), (4, 1), (6, 1), (8, 6), (9, 2), (12, 3), (13, 183), (24, 2), (121, 12345))))!).Element("result")!.Value);
            Data(extension.HandleWeb(Get($"{deckBase}kin/game/finish?SVOGameID=1&endStatus=1&gameMode=19&stats=" + StatsList((2, 90), (4, 1), (7, 1), (8, 3))))!);

            // personalstats: Cached slots by column, the version last (0x51bae4); friendsboard by name.
            string loginText = Encoding.UTF8.GetString(extension.HandleWeb(Get("/killzone3/kin/account/nplogin.do?id=dyllan501"))!.Body);
            Assert.Matches("<personalstats><2>150</2><8>6</8><9>2</9><12>3</12><13>183</13><24>2</24><version>[0-9]+</version></personalstats>", loginText);
            Assert.Contains("<friendsboard><player><accountid>10</accountid><accountname>dyllan501</accountname><GamePoints>150</GamePoints>", loginText);
            Assert.Contains("<player><accountid>11</accountid><accountname>dyllan500</accountname><GamePoints>90</GamePoints>", loginText);

            // playerstats: the other player's slots by column, with their name (0x8cea1c).
            string compare = Encoding.UTF8.GetString(extension.HandleWeb(Get($"{pcBase}kin/stats/playerstats?accountID=11"))!.Body);
            Assert.Contains("<playerstats><accountname>dyllan500</accountname><2>90</2><8>3</8><9>0</9>", compare);

            // friendsleaderboard: one board per asked gameMode (1,3,5,7 = Overall..Skirmish of region 0).
            XElement boards = Data(extension.HandleWeb(Get($"{deckBase}kin/stats/friendsleaderboard?gamemode=1%2C3%2C5%2C7"))!).Element("leaderboards")!;
            Assert.Equal(["Overall", "Warzone", "Operations", "Skirmish"], boards.Elements("leaderboard").Select(b => b.Element("name")!.Value));
            XElement overall = boards.Elements("leaderboard").First();
            Assert.Equal("dyllan500", overall.Element("mystats")!.Element("accountname")!.Value);
            Assert.Equal("90", overall.Element("mystats")!.Element("GamePoints")!.Value);
            Assert.Equal(["dyllan501", "dyllan500"], overall.Element("offline")!.Elements("player").Select(p => p.Element("accountname")!.Value));
            Assert.Equal("6", overall.Element("offline")!.Elements("player").First().Element("Kills")!.Value);
            Assert.NotNull(overall.Element("online"));
            Assert.Equal("0", boards.Elements("leaderboard").ElementAt(2).Element("offline")!.Elements("player").First().Element("GamePoints")!.Value);

            // friendtopleaderboard: the same entries as friendsboard, under leaderboard.
            XElement top = Data(extension.HandleWeb(Get($"{deckBase}kin/stats/friendtopleaderboard"))!).Element("leaderboard")!;
            Assert.Equal(["dyllan501", "dyllan500"], top.Elements("player").Select(p => p.Element("accountname")!.Value));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Title_file_loads_with_the_306_profile_and_the_web_root_as_svo_url()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "ServerApp-Titles", "Killzone3")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            return;
        string resources = Path.Combine(dir, "ServerApp-Titles", "Killzone3");
        List<string> problems = [];
        List<MediusTitle> loaded = MediusTitle.LoadAll(
            [new TitleDescriptor { TitleId = "killzone3", Name = "Killzone 3", ProtocolFamily = "medius", ResourceDir = resources }], problems);
        // The app ids are not known until the game first connects (the MUIS logs them); until
        // they are added the loader refuses the file, so its contents are checked directly.
        MediusTitle title = JsonSerializer.Deserialize<MediusTitle>(File.ReadAllText(Path.Combine(resources, MediusTitle.FileName)), MediusTitle.JsonOptions)!;
        if (title.AppIds.Count == 0)
            Assert.Contains("lists no AppIds", Assert.Single(problems));
        else
        {
            Assert.Empty(problems);
            title = Assert.Single(loaded);
        }
        Assert.Same(ScertLibraryProfile.Rt306, title.RtProfile);
        Assert.Equal("killzone3", title.Extension);
        Assert.EndsWith(":10091/killzone3/", Assert.Single(title.Universes).SvoUrl);
        Killzone3Settings settings = title.ExtensionSettings.Deserialize<Killzone3Settings>(MediusTitle.JsonOptions)!;
        Assert.Equal("/killzone3/", settings.Web.RootPath);
        Assert.Equal(6, settings.Web.Regions.Count);
        // finishgame's 133 stats columns; the live reports had GamesPlayed 1 at 4, GameLosses or
        // GameDraws 1 at 6/7 by endStatus, and the match time at 13 and 130 [live 2026-11-08].
        Assert.Equal(Enumerable.Range(0, 133), settings.Stats.Slots.Select(s => s.Column));
        Assert.Equal(["GamesPlayed", "GameLosses", "GameDraws", "TimePlayed", "TimePlayedSinceLagDetection"],
            new[] { 4, 6, 7, 13, 130 }.Select(c => settings.Stats.Slots[c].Id));
        Assert.Equal("wkillsPistol", settings.Stats.Slots[24].Key);
        Assert.Equal(Killzone3LeaderboardPlayers.All, settings.Stats.LeaderboardPlayers);
        // A lone party is matched at once (no second player needed) [live 2026-11-08: a 2-player
        // minimum left the client on "joining lobby"].
        Assert.Equal(1, title.Matchmaking.Default.MinPlayers);
        // The MatchParty answer KZ3 waits on, and the key its MatchFound callback checks [C 0x165f9c, 0x16608c].
        Assert.Equal(11, title.Matchmaking.PartyQueuedStatus);
        Assert.DoesNotContain(settings.Web.Urns, u => settings.Web.ScreenUrns.Contains(u.Name));
        Assert.Equal([0, 6], title.Matchmaking.FoundKey.Select(r => r.Offset));
    }

    /// <summary>
    /// urn:creategame is answered with result OK and a non-zero svogameid under the root
    /// (0x51e3c8); an empty document ended the match 30 s in [live 2026-11-08]. finishgame
    /// gets result OK.
    /// </summary>
    [Fact]
    public void Create_game_answers_ok_and_a_new_svogameid_and_finish_game_ok()
    {
        Killzone3Extension extension = Extension();
        MediusWebResponse created = extension.HandleWeb(Get("/killzone3/kin/game/create?gameName=G10-57713&scertGameID=23050&hostPlayerList=10&timePerDiv=965&numDivs=1&gameMode=23"))!;
        XElement data = Data(created);
        Assert.Equal("OK", data.Element("result")!.Value);
        long id = long.Parse(data.Element("svogameid")!.Value);
        Assert.NotEqual(0, id);

        XElement again = Data(extension.HandleWeb(Get("/killzone3/kin/game/create?gameName=G10-57714"))!);
        Assert.NotEqual(id, long.Parse(again.Element("svogameid")!.Value));

        XElement finished = Data(extension.HandleWeb(Get($"/killzone3/kin/game/finish?SVOGameID={id}"))!);
        Assert.Equal("OK", finished.Element("result")!.Value);
        Assert.Null(finished.Element("svogameid"));
    }

    /// <summary>
    /// urn:intel is the game's in-match Select screen; handing it out as web data crashed the
    /// game when Select was pressed [live 2026-11-08]. Screen URNs never reach the store.
    /// </summary>
    [Fact]
    public void Screen_urns_are_never_handed_out_at_login()
    {
        MediusTitle title = new()
        {
            ExtensionSettings = JsonDocument.Parse("""
                { "Web": { "Urns": [ { "Name": "intel", "Path": "kin/intel" }, { "Name": "playerstats", "Path": "kin/stats/playerstats" } ] } }
                """).RootElement,
        };
        Killzone3Extension extension = new() { FindAccount = name => new MediusAccount { AccountId = 1, Name = name } };
        extension.Initialize(title, null!);
        XElement data = LoginData(extension.HandleWeb(Get("/killzone3/kin/account/nplogin.do?id=helghast"))!);
        Assert.Equal(["playerstats"], data.Element("urnstore")!.Elements("urn").Select(u => u.Element("name")!.Value));
    }

    private static Killzone3Extension Extension(params MediusAccount[] accounts)
    {
        MediusTitle title = new()
        {
            ExtensionSettings = JsonDocument.Parse("""
                { "Web": { "RootPath": "/killzone3/",
                           "Regions": [ { "Id": 1, "MediusLocation": 0 }, { "Id": 4, "MediusLocation": 2 } ],
                           "Urns": [ { "Name": "playerstats", "Path": "kin/stats/playerstats", "Answer": "PlayerStats" },
                                     { "Name": "creategame", "Path": "kin/game/create", "Answer": "CreateGame" },
                                     { "Name": "finishgame", "Path": "kin/game/finish", "Answer": "FinishGame" },
                                     { "Name": "friendsleaderboard", "Path": "kin/stats/friendsleaderboard", "Answer": "FriendsLeaderboard" },
                                     { "Name": "friendtopleaderboard", "Path": "kin/stats/friendtopleaderboard", "Answer": "FriendTopLeaderboard" } ],
                           "ClientConfig": { "MaxPartySize": "8" } } }
                """).RootElement,
        };
        Killzone3Extension extension = new() { FindAccount = name => accounts.FirstOrDefault(a => a.Name == name) };
        extension.Initialize(title, null!);
        return extension;
    }

    private static MediusWebRequest Get(string target) =>
        new() { Method = "GET", Target = target, Headers = new Dictionary<string, string> { ["host"] = "10.0.0.5:10091" } };

    /// <summary>A page's data document: <c>data</c> is the root, whose children the page binds.</summary>
    private static XElement Data(MediusWebResponse response)
    {
        Assert.Equal(200, response.Status);
        Assert.Equal("text/xml", response.ContentType);
        XElement root = XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root!;
        Assert.Equal("data", root.Name.LocalName);
        return root;
    }

    /// <summary>
    /// The login answer: the parser hands 0x8b7028 the root element, which looks for a
    /// <c>data</c> child; <c>data</c> as the root itself was error 20002 [live 2026-11-08].
    /// </summary>
    private static XElement LoginData(MediusWebResponse response)
    {
        Assert.Equal(200, response.Status);
        XElement root = XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root!;
        Assert.Equal("xml", root.Name.LocalName);
        return Assert.Single(root.Elements("data"));
    }

    [Fact]
    public void Login_answers_30000_a_nonce_and_urns_under_the_session()
    {
        Killzone3Extension extension = Extension(new MediusAccount { AccountId = 42, Name = "helghast" });
        XElement data = LoginData(extension.HandleWeb(Get("/killzone3/kin/account/nplogin.do?id=helghast&sigclass=0&version=65536"))!);
        Assert.Equal("30000", data.Element("loginresult")!.Value);
        Assert.Equal(32, data.Element("nonce")!.Value.Length);
        Assert.Equal(5, data.Element("urnstore")!.Elements("urn").Count());
        XElement urn = data.Element("urnstore")!.Elements("urn").First();
        Assert.Equal("playerstats", urn.Element("name")!.Value);
        string uri = urn.Element("uri")!.Value;
        Assert.Matches("^http://10\\.0\\.0\\.5:10091/killzone3/s/[0-9a-f]{32}/kin/stats/playerstats$", uri);
        Assert.Equal("8", data.Element("client-config")!.Element("MaxPartySize")!.Value);

        // The URN's own request is answered (no stats kept without a data directory).
        MediusWebResponse stats = extension.HandleWeb(Get(new Uri(uri).AbsolutePath))!;
        Assert.Equal("", Data(stats).Element("playerstats")!.Element("accountname")!.Value);
    }

    [Fact]
    public void Login_of_an_unknown_player_is_not_30000()
    {
        XElement data = LoginData(Extension().HandleWeb(Get("/killzone3/kin/account/nplogin.do?id=nobody"))!);
        Assert.NotEqual("30000", data.Element("loginresult")!.Value);
        Assert.Null(data.Element("urnstore"));
    }

    [Fact]
    public void Region_list_names_each_region_and_its_medius_location()
    {
        XElement data = Data(Extension().HandleWeb(Get("/killzone3/kin/stats/region_list.jsp"))!);
        Assert.Equal(["1:0", "4:2"], data.Elements("region").Select(r => $"{r.Element("id")!.Value}:{r.Element("mediuslocation")!.Value}"));
    }

    [Fact]
    public void Paths_outside_the_root_are_left_to_other_services()
    {
        Assert.Null(Extension().HandleWeb(Get("/killzone2/index?languageID=1")));
        Assert.Empty(Extension().WebHosts);
        Assert.Equal(404, Extension().HandleWeb(Get("/killzone3/unknown.jsp"))!.Status);
    }

    [Fact]
    public void Mac_recipe_names_the_inputs_that_reproduce_a_request_mac()
    {
        MediusWebRequest request = Get("/killzone3/kin/stats/playerstats?id=3");
        string mac = Killzone3Mac.Compute("/killzone3/kin/stats/playerstats", "id=3", [], "abc");
        Assert.Equal("path+query", Killzone3Mac.Recipe(request, "/killzone3/", "abc", mac));
        Assert.Equal("path+query (no nonce)", Killzone3Mac.Recipe(request, "/killzone3/", "abc", Killzone3Mac.Compute("/killzone3/kin/stats/playerstats", "id=3", [], "")));
        Assert.Null(Killzone3Mac.Recipe(request, "/killzone3/", "abc", "00"));
    }
}
