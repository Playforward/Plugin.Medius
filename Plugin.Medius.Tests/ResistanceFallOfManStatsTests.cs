using Plugin.Medius.Extensions.ResistanceFallOfMan;
using Plugin.Medius.Lobby;
using Plugin.Medius.Web;
using System.Text;
using System.Text.Json;

namespace Plugin.Medius.Tests;

/// <summary>Career stats from finishGame forms, using the Deck's and PC's own reports of a game (2026-11-06).</summary>
public sealed class ResistanceFallOfManStatsTests : IDisposable
{
    private const int Player = 9;
    private const string Won = "SVOGameID=1&endStatus=1&gameMode=9&mapID=200&modeID=9&counter=1&wins=1&losses=0&money=0&level=2&rating=10&kills=1&deaths=0&assists=0&headshots=1&shotsFired=38&rifleKills=1&rifleHits=14&rifleMiss=10&killSpree=1&lifeSpan=166";
    private const string Dropped = "SVOGameID=2&endStatus=4&gameMode=10&mapID=200&modeID=10&counter=2&wins=0&losses=1&money=0&level=3&rating=0&kills=3&deaths=2&killSpree=0&lifeSpan=20";

    private readonly string _dir = Directory.CreateTempSubdirectory("rfom-stats").FullName;
    private readonly ResistanceStats _stats;

    public ResistanceFallOfManStatsTests() => _stats = new ResistanceStats(new AccountDataStore(_dir), "resistance");

    public void Dispose() => Directory.Delete(_dir, true);

    private void Post(string form) => _stats.Add(Player, MediusWebRequest.ParseQuery(form));

    [Fact]
    public void A_match_shows_up_under_the_clients_stat_names()
    {
        Post(Won);
        Dictionary<string, string> view = _stats.View(Player, 9);
        Assert.Equal(("1", "0", "1", "1"), (view["WINS"], view["LOSSES"], view["KILLS"], view["GAMES_PLAYED"]));
        Assert.Equal(("10", "1", "38"), (view["RATING_SCORE"], view["RIFILE_KILLS"], view["SHOTS_FIRED"]));
        Assert.Equal("58", view["RIFILE_ACC"]);
        Assert.Equal("1", view["KD_RATIO"]);
        Assert.Equal("166", view["AVG_LIFESPAN"]);
    }

    [Fact]
    public void A_posted_game_reaches_the_players_stats_page_once()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "medius", "t"));
        File.WriteAllText(Path.Combine(_dir, "medius", "t", "playerstats.xml"),
            """<XML><Career_Leaderboard userID="{Query.PlayerID}"><WINS name="WINS">0</WINS><KILLS name="KILLS">0</KILLS><SP name="SP">0</SP></Career_Leaderboard></XML>""");
        ResistanceExtension extension = new() { Stats = _stats };
        extension.Initialize(new MediusTitle
        {
            AppIds = [20174],
            Descriptor = new ServerShared.Titles.TitleDescriptor { TitleId = "resistance", ResourceDir = _dir },
            ExtensionSettings = JsonDocument.Parse("""
                { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test:10091", "TemplatesDir": "t",
                           "Uris": [ { "Name": "finishGameURI", "Value": "/resistancefom/game/finishGame", "DataType": "DATA" },
                                     { "Name": "playerStatsURI", "Value": "/resistancefom/stats/playerStats?PlayerID=%d&gameMode=%d", "DataType": "DATA" } ],
                           "Pages": [ { "Uri": "playerStatsURI", "Template": "playerstats.xml", "ContentType": "text/xml;charset=UTF-8" } ] } }
                """).RootElement,
        }, null!);
        int game = extension.Games.Create("RFOM_3", Player, 9, 180, 1).Id;
        Dictionary<string, string> headers = new() { ["host"] = "svo.test", ["cookie"] = "AuthKey=" + extension.StartSession(Player) };
        MediusWebRequest finish = new()
        {
            Method = "POST", Target = "/resistancefom/game/finishGame", Headers = headers,
            Body = Encoding.ASCII.GetBytes(Won.Replace("SVOGameID=1", $"SVOGameID={game}")),
        };

        Assert.Equal(200, extension.HandleWeb(finish)!.Status);
        Assert.Equal(200, extension.HandleWeb(finish)!.Status);

        MediusWebResponse page = extension.HandleWeb(new MediusWebRequest { Target = $"/resistancefom/stats/playerStats?PlayerID={Player}&gameMode=15", Headers = headers })!;
        string text = Encoding.UTF8.GetString(page.Body);
        Assert.Contains("""<WINS name="WINS">1</WINS>""", text);
        Assert.Contains("""<KILLS name="KILLS">1</KILLS>""", text);
        Assert.Contains("""<SP name="SP">0</SP>""", text);
    }

    [Fact]
    public void Matches_add_up_and_survive_a_reload()
    {
        Post(Won);
        Post(Won);
        Post(Dropped);

        Dictionary<string, string> career = new ResistanceStats(new AccountDataStore(_dir), "resistance").View(Player, 15);
        Assert.Equal(("2", "1", "5", "2"), (career["WINS"], career["LOSSES"], career["KILLS"], career["DEATHS"]));
        Assert.Equal(("3", "1", "2"), (career["GAMES_PLAYED"], career["INCOMPLETES"], career["STREAKS"]));
        Assert.Equal("2.5", career["KD_RATIO"]);
        Assert.Equal("2", career["SP_COUNTER"]);

        // A mode's page shows that mode only; another player has nothing.
        Assert.Equal("2", _stats.View(Player, 9)["WINS"]);
        Assert.Equal("0", _stats.View(Player, 10)["WINS"]);
        Assert.Equal("0", _stats.View(Player + 1, 15)["WINS"]);
    }
}
