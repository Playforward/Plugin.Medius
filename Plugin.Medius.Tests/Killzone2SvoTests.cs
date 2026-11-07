using Plugin.Medius.Extensions.Killzone2;
using Plugin.Medius.Web;
using ServerShared.Titles;
using System.Text;
using System.Text.Json;

namespace Plugin.Medius.Tests;

/// <summary>
/// Killzone 2's SVO web service (Extensions/Killzone2): the X-SVOMac both ways and
/// the documents the 1.29 client's tag modules read (addresses in the extension).
/// </summary>
public sealed class Killzone2SvoTests
{
    // Sent by Killzone 2 1.29 with its first SVO request, GET / with no cookie or body [live 2026-10-30].
    private const string LiveRootMac = "5014b6872cafea54664b932eaafa1596";
    private const string ResponseKey = "sp9ck0348sld00000000000000000000";

    private static Killzone2Extension Extension()
    {
        MediusTitle title = new()
        {
            ExtensionSettings = JsonDocument.Parse("""
                { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "https://svo.test:10091", "ResponseMacKey": "sp9ck0348sld00000000000000000000",
                           "BrowserInit": "init",
                           "Uris": [ { "Name": "TicketLoginURI", "Value": "https:ticket/login" },
                                     { "Name": "homeURI", "Value": "/killzone2/home" },
                                     { "Name": "createGamePlayerURI", "Value": "http://other.test/game/player", "DataType": "DATA",
                                       "Params": [ { "Name": "SVOGameID", "Type": "integer" } ] } ] } }
                """).RootElement,
        };
        Killzone2Extension extension = new();
        extension.Initialize(title, null!);
        return extension;
    }

    [Fact]
    public void Request_mac_is_md5_of_path_cookie_body_and_scheme()
    {
        Assert.Equal(LiveRootMac, SvoMac.Request("/", null, [], "http://"));
        Assert.NotEqual(LiveRootMac, SvoMac.Request("/", "id=1", [], "http://"));
    }

    /// <summary>The DATA layout of PS Rewired's root [live 2026-10-31].</summary>
    [Fact]
    public void Root_lists_the_named_uris_as_data_elements()
    {
        Assert.Equal("<XML><URL_List>" +
                     "<DATA dataType=\"URI\" name=\"TicketLoginURI\" value=\"https:ticket/login\"/>" +
                     "<DATA dataType=\"URI\" name=\"homeURI\" value=\"https://svo.test:10091/killzone2/home\"/>" +
                     "<DATA dataType=\"DATA\" name=\"createGamePlayerURI\" value=\"http://other.test/game/player\"><ServerParams>" +
                     "<Param name=\"SVOGameID\" optional=\"false\" type=\"integer\"/></ServerParams></DATA>" +
                     "<BROWSER_INIT name=\"init\"/></URL_List></XML>", Extension().UriList());
    }

    /// <summary>PS Rewired's answer to GET /killzone2/index?languageID=1: the MAC covers the query too [live 2026-10-31].</summary>
    [Fact]
    public void Mac_covers_the_query_and_matches_ps_rewireds_answer()
    {
        string request = SvoMac.Request("/killzone2/index?languageID=1", null, [], "http://");
        Assert.Equal("8278b53fbf31d1bc2ba304cbb7ba61ab", SvoMac.Response(request, ResponseKey));
    }

    [Fact]
    public void Answers_carry_the_response_mac_the_client_would_expect()
    {
        MediusWebRequest request = new()
        {
            Method = "GET", Target = "/",
            Headers = new Dictionary<string, string> { ["host"] = "svo.test", ["x-svomac"] = LiveRootMac },
        };
        MediusWebResponse response = Extension().HandleWeb(request)!;
        Assert.Equal("text/xml", response.ContentType);
        Assert.Equal(SvoMac.Response(LiveRootMac, ResponseKey), response.Headers["X-SVOMac"]);
        Assert.Contains($"X-SVOMac: {response.Headers["X-SVOMac"]}\r\n", Encoding.ASCII.GetString(response.Serialize(false)));
    }

    [Fact]
    public void Login_element_is_ps_rewireds_child_element_form()
    {
        // PS Rewired's answer (POST to their https:ticket/login, 2026-10-31), whitespace aside [live].
        Assert.Equal("<SP_Login><status><id>20600</id></status><userContext>0</userContext></SP_Login>", Extension().LoginElement(6, 20600));
    }

    /// <summary>ServerApp-Titles/Killzone2 loads with the 3.03 profile and a TicketLoginURI the client's https check passes; skipped without that checkout.</summary>
    [Fact]
    public void Killzone2_title_file_lists_an_https_ticket_login()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "ServerApp-Titles", "Killzone2")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            return;
        List<string> problems = [];
        MediusTitle title = Assert.Single(MediusTitle.LoadAll(
            [new TitleDescriptor { TitleId = "killzone2", Name = "Killzone 2", ProtocolFamily = "medius", ResourceDir = Path.Combine(dir, "ServerApp-Titles", "Killzone2") }],
            problems));
        Assert.Empty(problems);
        Assert.Equal("3.03", title.RtProfile.Version);
        Assert.Equal("killzone2", title.Extension);
        Killzone2Settings settings = title.ExtensionSettings.Deserialize<Killzone2Settings>(MediusTitle.JsonOptions)!;
        Assert.Contains(settings.Svo.Uris, u => u.Name == "TicketLoginURI" && u.Value.StartsWith("https:"));

        // Every board answers a listed URI from a template whose placeholders are all there, and the
        // template is well-formed once the disc's group closers (<_i: name="!" g="2" />) are set aside.
        Assert.NotEmpty(settings.Svo.Boards);
        foreach (Killzone2BoardSettings board in settings.Svo.Boards)
        {
            Assert.Contains(settings.Svo.Uris, u => u.Name == board.Uri);
            string template = File.ReadAllText(Path.Combine(dir, "ServerApp-Titles", "Killzone2", "medius", settings.Svo.TemplatesDir, board.Template));
            foreach (string placeholder in new[] { "{BoardTitle}", "{BoardDescription}", "{BoardEmpty}", "{BoardRows}" })
                Assert.Contains(placeholder, template);
            System.Xml.Linq.XDocument.Parse(System.Text.RegularExpressions.Regex.Replace(template, "<_([a-z]):", "<_$1_close").Replace("{BoardRows}", "{ }"));
        }
        Assert.Contains(settings.Svo.Boards, b => b.Uri == "leaderboardURI" && b.Source == Killzone2BoardSource.Players);

        // Every template page answers a listed URI, its values leave no placeholder of theirs behind, and
        // the filled page is well-formed the same way.
        foreach (Killzone2PageSettings page in settings.Svo.Pages)
        {
            Assert.Contains(settings.Svo.Uris, u => u.Name == page.Uri);
            string filled = Killzone2Extension.FillValues(
                File.ReadAllText(Path.Combine(dir, "ServerApp-Titles", "Killzone2", "medius", settings.Svo.TemplatesDir, page.Template)), page.Values);
            filled = Killzone2Extension.FillProfile(filled, null).Replace("{TimeZones}", Killzone2Extension.TimeZonesLua());
            Assert.DoesNotMatch(@"\{(?!PlayerName\}|AccountId\}|BaseUrl\}|PublicHost\})[A-Z][A-Za-z]*(\.lua)?\}", filled);
            System.Xml.Linq.XDocument.Parse(System.Text.RegularExpressions.Regex.Replace(filled, "<_([a-z]):", "<_$1_close"));
        }
        Assert.Contains(settings.Svo.Pages, p => p.Uri == "friendsURI" && p.Values["Mode"] == "friends");
        Assert.Contains(settings.Svo.Pages, p => p.Uri == "mailboxURI" && p.Values["Mode"] == "mail");
        foreach (string uri in new[] { "personalStatsURI", "viewtimezonesURI", "logoutURI" })
            Assert.Contains(settings.Svo.Pages, p => p.Uri == uri);
        Assert.Contains(settings.Svo.Boards, b => b.Uri == "leaderboardFriendsURI" && b.Source == Killzone2BoardSource.Friends);
        Assert.Contains(settings.Svo.Boards, b => b.Uri == "clanTournamentURI");

        // The clan page's template fills completely and is well-formed.
        string clan = Killzone2Extension.FillValues(File.ReadAllText(Path.Combine(dir, "ServerApp-Titles", "Killzone2", "medius", settings.Svo.TemplatesDir, settings.Svo.Clans.Template)), settings.Svo.Clans.Values)
            .Replace("{ClanData}", "nil").Replace("{ClanPendingName.lua}", "\"\"").Replace("{ClanUrl.lua}", "\"http://x/killzone2/clans\"").Replace("{ClanInvites}", "{ }").Replace("{ClanMessage.lua}", "\"\"")
            .Replace("{ClanNameMax}", "31").Replace("{ClanTagMax}", "4");
        clan = Killzone2Extension.FillClan(Killzone2Extension.FillProfile(clan, null), null);
        Assert.DoesNotMatch(@"\{(?!PlayerName\}|AccountId\}|BaseUrl\}|PublicHost\})[A-Z][A-Za-z]*(\.lua)?\}", clan);
        System.Xml.Linq.XDocument.Parse(System.Text.RegularExpressions.Regex.Replace(clan, "<_([a-z]):", "<_$1_close"));

        // The home page pushes the server's score and minimum rank at sign-in.
        string home = File.ReadAllText(Path.Combine(dir, "ServerApp-Titles", "Killzone2", "medius", settings.Svo.PagesDir, "killzone2", "home.svml"));
        Assert.Contains("{ProfileSyncScore}", home);
        Assert.Contains("{ProfileSyncRank}", home);
        Assert.Contains("{ProfileClanId}", home);
    }

    [Fact]
    public void Profile_sync_values_and_time_zones()
    {
        Assert.Equal("0 -1", Killzone2Extension.FillProfile("{ProfileSyncScore} {ProfileSyncRank}", null));
        Assert.Equal("1500 2", Killzone2Extension.FillProfile("{ProfileSyncScore} {ProfileSyncRank}", new Killzone2Profile { MPScore = 1500, MinScore = 10, MinRank = 2 }));
        Assert.Equal("40 -1", Killzone2Extension.FillProfile("{ProfileSyncScore} {ProfileSyncRank}", new Killzone2Profile { MPScore = 5, MinScore = 40 }));
        string zones = Killzone2Extension.TimeZonesLua();
        Assert.StartsWith("{ ", zones);
        Assert.Contains(Killzone2Extension.LuaString("UTC+00:00"), zones);
        Assert.Contains(Killzone2Extension.LuaString("UTC-05:00"), zones);
    }

    [Fact]
    public void Page_values_fill_as_text_and_as_lua_strings()
    {
        Assert.Equal("<_t>A &amp; B</_t> gMode = \"a\\038b\"",
            Killzone2Extension.FillValues("<_t>{Title}</_t> gMode = {Mode.lua}", new Dictionary<string, string> { ["Title"] = "A & B", ["Mode"] = "a&b" }));
    }

    // KZ2 1.29's ticket login to PS Rewired's "https:ticket/login", sent to our server [live 2026-10-31].
    private const string TicketTarget = "/killzone2/https:ticket/login?sig=FT2mx0Gt2EhHNgobZZkLCg%3d%3d&userContext=0&sigclass=0&siglength=16&timeZone=1&languageID=2";
    private const string TicketHex = "21010000000000F7300000A4000800143235373138350000000000000000000000000000000100040000010000070008000001A19083208400070008000001A19090DC240002000800000000000755CA0004002064796C6C616E3530310000000000000000000000000000000000000000000000000800046272000000040004756E0000000800184550393030302D4243455330303038315F30300000000000000100040000000000000000000000003002004B000800045250434E00";

    [Fact]
    public void Ticket_login_mac_hashes_the_form_fields_not_the_ticket()
    {
        Assert.Equal("07586fd7eedf474bde3f0f6695772026",
            SvoMac.Request(TicketTarget, null, Encoding.ASCII.GetBytes(TicketTarget[(TicketTarget.IndexOf('?') + 1)..]), "http://"));
    }

    [Fact]
    public void Ticket_login_to_the_hostless_uri_is_answered_with_sp_login()
    {
        MediusWebRequest request = new()
        {
            Method = "POST", Target = TicketTarget, Body = Convert.FromHexString(TicketHex),
            Headers = new Dictionary<string, string> { ["host"] = "svo.test", ["x-svomac"] = "07586fd7eedf474bde3f0f6695772026", ["content-type"] = "application/octet-stream" },
        };
        MediusWebResponse response = Extension().HandleWeb(request)!;
        Assert.Equal("text/xml", response.ContentType);
        Assert.Contains("<XML><SP_Login><status><id>", Encoding.UTF8.GetString(response.Body));
        Assert.Equal(SvoMac.Response("07586fd7eedf474bde3f0f6695772026", ResponseKey), response.Headers["X-SVOMac"]);
    }

    [Fact]
    public void Pages_come_from_the_title_page_directory_and_stay_inside_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kz2-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "medius", "svo", "killzone2"));
        File.WriteAllText(Path.Combine(dir, "medius", "svo", "killzone2", "home.svml"), "<SVML><_t>{BaseUrl} {PlayerName}</_t></SVML>");
        File.WriteAllText(Path.Combine(dir, "medius", "secret.svml"), "outside");
        try
        {
            MediusTitle title = new()
            {
                Descriptor = new TitleDescriptor { TitleId = "killzone2", ResourceDir = dir },
                ExtensionSettings = JsonDocument.Parse("""{ "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test", "PagesDir": "svo" } }""").RootElement,
            };
            Killzone2Extension extension = new();
            extension.Initialize(title, null!);
            MediusWebResponse? Get(string target) => extension.HandleWeb(new MediusWebRequest
            {
                Method = "GET", Target = target, Headers = new Dictionary<string, string> { ["host"] = "svo.test" },
            });

            MediusWebResponse home = Get("/killzone2/home")!;
            Assert.Equal("text/svml;charset=UTF-8", home.ContentType);
            Assert.Equal("<SVML><_t>http://svo.test </_t></SVML>", Encoding.UTF8.GetString(home.Body));
            Assert.Null(Get("/killzone2/../../secret"));
            Assert.Null(Get("/killzone2/missing"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Score_processor_calls_get_pages_that_call_it_back()
    {
        Killzone2Extension extension = new();
        extension.Initialize(new MediusTitle
        {
            ExtensionSettings = JsonDocument.Parse("""
                { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test",
                           "Uris": [ { "Name": "gameCreateURL", "Value": "/killzone2/game/create" },
                                     { "Name": "gameJoinURL", "Value": "/killzone2/game/join" },
                                     { "Name": "gameSubmitURL", "Value": "/killzone2/game/submit" },
                                     { "Name": "createGameURI", "Value": "/killzone2/game/create2" },
                                     { "Name": "createGamePlayerURI", "Value": "/killzone2/game/player" } ] } }
                """).RootElement,
        }, null!);
        MediusWebResponse? Get(string target) => extension.HandleWeb(new MediusWebRequest
        {
            Method = "GET", Target = target, Headers = new Dictionary<string, string> { ["host"] = "svo.test" },
        });

        // The host's call when the match started [live 2026-10-31].
        const string live = "/killzone2/game/create?gameName=G6-162478&scertGameID=20400&hostPlayerList=6&remotePlayerList=&timePerDiv=17760&numDivs=1&gameMode=0";
        // The score processor opens it as a popup and waits for SetSVOGameID.
        MediusWebResponse created = Get(live)!;
        Assert.Equal("text/svml;charset=UTF-8", created.ContentType);
        string page = Encoding.UTF8.GetString(created.Body);
        Assert.Contains("gScoreProcessor:SetSVOGameID(1)", page);
        Assert.Contains("gBrowser:ClosePopup()", page);
        System.Xml.Linq.XDocument.Parse(page);
        SvoGame game = extension.Games.Find(1)!;
        Assert.Equal(("G6-162478", 6, 0, 17760), (game.Name, game.HostAccountId, game.GameMode, game.TimePerDivision));
        Assert.Contains("SetSVOGameID(-1)", Encoding.UTF8.GetString(Get("/killzone2/game/create?gameName=&gameMode=0")!.Body));

        Assert.Contains("SetSVOGameID(1)", Encoding.UTF8.GetString(Get("/killzone2/game/join?SVOGameID=1")!.Body));
        Assert.Contains("SetSVOGameID(-1)", Encoding.UTF8.GetString(Get("/killzone2/game/join?SVOGameID=99")!.Body));
        Assert.Contains("gScoreProcessor:Close()", Encoding.UTF8.GetString(Get("/killzone2/game/submit?SVOGameID=1&endStatus=0&gameMode=0")!.Body));

        // svoclient's own registration is read by CreateGameTagModule.
        Assert.Contains("<XML><Create_Game><status><message>GAME_CREATE_SUCCESS</message></status><gameID>2</gameID></Create_Game></XML>",
            Encoding.UTF8.GetString(Get(live.Replace("/create?", "/create2?"))!.Body));

        // A player without an SVO session, or of an unknown game, is refused (any id but 20422).
        Assert.Contains("<XML><GamePlayer><status><id>20423</id></status></GamePlayer></XML>",
            Encoding.UTF8.GetString(Get("/killzone2/game/player?SVOGameID=1&playerSide=0")!.Body));
        Assert.Equal("<GamePlayer><status><id>20422</id></status></GamePlayer>", Killzone2Extension.GamePlayerElement(20422));
    }

    [Fact]
    public void A_match_result_adds_its_points_to_the_stored_score()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kz2-submit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Killzone2Extension extension = new();
            extension.Initialize(new MediusTitle
            {
                Descriptor = new TitleDescriptor { TitleId = "killzone2", ResourceDir = dir },
                ExtensionSettings = JsonDocument.Parse("""
                    { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test",
                               "Uris": [ { "Name": "gameCreateURL", "Value": "/killzone2/game/create" },
                                         { "Name": "gameJoinURL", "Value": "/killzone2/game/join" },
                                         { "Name": "gameSubmitURL", "Value": "/killzone2/game/submit" } ] } }
                    """).RootElement,
            }, null!);
            Dictionary<int, string> names = new() { [6] = "dyllan501", [7] = "dyllan500" };
            extension.Profiles = new Killzone2Profiles(new Lobby.AccountDataStore(dir), "killzone2", id => names.GetValueOrDefault(id));
            string host = extension.StartSession(6), joiner = extension.StartSession(7);
            string Get(string target, string session) => Encoding.UTF8.GetString(extension.HandleWeb(new MediusWebRequest
            {
                Method = "GET", Target = target, Headers = new Dictionary<string, string> { ["host"] = "svo.test", ["cookie"] = $"AuthKey={session}" },
            })!.Body);

            Get("/killzone2/game/create?gameName=G6-175441&scertGameID=20400&hostPlayerList=6&remotePlayerList=&timePerDiv=17760&numDivs=1&gameMode=0", host);
            Get("/killzone2/game/join?SVOGameID=1", joiner);
            // Each player's result [live 2026-11-01, abridged]: the winner's pts=6 was their profile's gain.
            const string won = "/killzone2/game/submit?SVOGameID=1&endStatus=1&gameMode=0&pts=6&gc=0&gj=1&gp=1&win=1&los=0&ki=0&de=1";
            Assert.Contains("gScoreProcessor:Close()", Get(won, joiner));
            Get("/killzone2/game/submit?SVOGameID=1&endStatus=1&gameMode=0&pts=0&gc=1&gj=0&gp=1&win=0&los=1", host);
            Assert.Equal(6, extension.Profiles.Find(7)!.MPScore);
            Assert.Equal(0, extension.Profiles.Find(6)!.MPScore);

            // A repeated submit is not counted twice, and the game's next upload sends the same total.
            Get(won, joiner);
            Assert.Equal(6, extension.Profiles.Find(7)!.MPScore);
            // An unknown game adds nothing.
            Get("/killzone2/game/submit?SVOGameID=9&endStatus=1&gameMode=0&pts=50", joiner);
            Assert.Equal(6, extension.Profiles.Find(7)!.MPScore);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Lua_strings_escape_everything_that_could_end_them()
    {
        Assert.Equal("\"dyllan501\"", Killzone2Extension.LuaString("dyllan501"));
        Assert.Equal("\"a\\034\\093\\093\\062\\092\"", Killzone2Extension.LuaString("a\"]]>\\"));
    }

    [Fact]
    public void Profile_uploads_feed_the_player_board()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kz2-boards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "medius", "svo", "killzone2", "profile", "sync"));
        Directory.CreateDirectory(Path.Combine(dir, "medius", "svo-templates"));
        File.WriteAllText(Path.Combine(dir, "medius", "svo", "killzone2", "profile", "sync", "upload.svml"), "<SVML>uploaded</SVML>");
        File.WriteAllText(Path.Combine(dir, "medius", "svo-templates", "board.svml"), "<SVML><_t>{BoardTitle}|{BoardEmpty}</_t><_s><![CDATA[global gRows = {BoardRows}]]></_s></SVML>");
        try
        {
            Killzone2Extension extension = new();
            extension.Initialize(new MediusTitle
            {
                Descriptor = new TitleDescriptor { TitleId = "killzone2", ResourceDir = dir },
                ExtensionSettings = JsonDocument.Parse("""
                    { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test", "PagesDir": "svo", "TemplatesDir": "svo-templates",
                               "ProfileUploadPath": "/killzone2/profile/sync/upload",
                               "Uris": [ { "Name": "leaderboardURI", "Value": "/killzone2/leaderboards.jsp" },
                                         { "Name": "leaderboardClanURI", "Value": "/killzone2/clans/leaderboards" },
                                         { "Name": "leaderboardFriendsURI", "Value": "/killzone2/leaderboards/friends" } ],
                               "Boards": [ { "Uri": "leaderboardURI", "Template": "board.svml", "Source": "Players", "Title": "RANKINGS", "Empty": "none" },
                                           { "Uri": "leaderboardClanURI", "Template": "board.svml", "Source": "Clans", "Title": "CLANS", "Empty": "No clans & no events" },
                                           { "Uri": "leaderboardFriendsURI", "Template": "board.svml", "Source": "Friends", "Title": "FRIENDS", "Empty": "none" } ] } }
                    """).RootElement,
            }, null!);
            Dictionary<int, string> names = new() { [6] = "dyllan501", [7] = "dyllan500" };
            extension.Profiles = new Killzone2Profiles(new Lobby.AccountDataStore(dir), "killzone2", id => names.GetValueOrDefault(id));
            string host = extension.StartSession(6), joiner = extension.StartSession(7);
            string Get(string target, string session) => Encoding.UTF8.GetString(extension.HandleWeb(new MediusWebRequest
            {
                Method = "GET", Target = target, Headers = new Dictionary<string, string> { ["host"] = "svo.test", ["cookie"] = $"AuthKey={session}" },
            })!.Body);

            // The sync page's upload [live 2026-10-31], then a higher score from the other player.
            Assert.Equal("<SVML>uploaded</SVML>", Get("/killzone2/profile/sync/upload?MPScore=0&Kill=0&GoodConduct=0&Tutorial=0", host));
            Get("/killzone2/profile/sync/upload?MPScore=1500&Kill=12", joiner);
            Killzone2Profile saved = extension.Profiles.Find(7)!;
            Assert.Equal((1500, 12), (saved.MPScore, saved.Ribbons["Kill"]));

            // An owner's MinRank survives the player's next upload.
            Lobby.AccountDataStore store = new(dir);
            saved.MinRank = 2;
            store.Write("killzone2", 7, Killzone2Profiles.Key, saved);
            Get("/killzone2/profile/sync/upload?MPScore=1500&Kill=13", joiner);
            Assert.Equal((2, 13), (extension.Profiles.Find(7)!.MinRank, extension.Profiles.Find(7)!.Ribbons["Kill"]));

            Assert.Equal("<SVML><_t>RANKINGS|none</_t><_s><![CDATA[global gRows = { " +
                         "{ pos = 1, name = \"dyllan500\", score = 1500, rank = 1, me = 0 }, " +
                         "{ pos = 2, name = \"dyllan501\", score = 0, rank = 1, me = 1 }, }]]></_s></SVML>",
                Get("/killzone2/leaderboards.jsp", host));
            Assert.Equal("<SVML><_t>CLANS|No clans &amp; no events</_t><_s><![CDATA[global gRows = { }]]></_s></SVML>",
                Get("/killzone2/clans/leaderboards", host));

            // Friends: the player and their buddies only.
            extension.FriendsOf = id => id == 6 ? [7] : [];
            Assert.Equal("<SVML><_t>FRIENDS|none</_t><_s><![CDATA[global gRows = { " +
                         "{ pos = 1, name = \"dyllan500\", score = 1500, rank = 1, me = 0 }, " +
                         "{ pos = 2, name = \"dyllan501\", score = 0, rank = 1, me = 1 }, }]]></_s></SVML>",
                Get("/killzone2/leaderboards/friends", host));
            extension.FriendsOf = _ => [];
            Assert.Equal("<SVML><_t>FRIENDS|none</_t><_s><![CDATA[global gRows = { " +
                         "{ pos = 1, name = \"dyllan501\", score = 0, rank = 1, me = 1 }, }]]></_s></SVML>",
                Get("/killzone2/leaderboards/friends", host));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
