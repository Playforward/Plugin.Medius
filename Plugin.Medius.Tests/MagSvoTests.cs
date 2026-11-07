using System.Text;
using System.Xml.Linq;
using Plugin.Medius.Extensions.Mag;
using Plugin.Medius.Web;

namespace Plugin.Medius.Tests;

/// <summary>MAG's SVO pages in the shapes the 2.12 client parses (login 0x11fc28, career stats 0x72d9bc).</summary>
public sealed class MagSvoTests
{
    private static (MagSvo Svo, MagCharacter Soldier) Service()
    {
        MagStatTable table = MagStatTable.Embedded(MagClientVersion.Patch212);
        MagCharacter soldier = new() { Name = "Raven1", Experience = 900 };
        soldier.RecordGame("GameType64", new Dictionary<string, long> { ["EnemiesKilled"] = 5, ["BattlesCompleted"] = 1 }, table);
        soldier.RecordGame("GameType128", new Dictionary<string, long> { ["EnemiesKilled"] = 2 }, table);
        MagCharacter other = new() { Name = "Valor1" };
        other.RecordGame("GameType64", new Dictionary<string, long> { ["EnemiesKilled"] = 1 }, table);
        MagSettings settings = new() { Protocol = MagProtocols.Patch212 };
        return (new MagSvo(settings, id => id == 7 ? soldier : null, () => [soldier, other], id => id == 7 ? "dyllan" : null), soldier);
    }

    private static XElement Get(MagSvo svo, string target)
    {
        MediusWebResponse response = svo.Serve(new MediusWebRequest { Target = target })!;
        Assert.Equal("text/xml", response.ContentType);
        return XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root!;
    }

    [Fact]
    public void The_login_names_the_account_and_says_it_succeeded()
    {
        XElement root = Get(Service().Svo, "/mag/svo/account/SP_Login_Submit_MAG.do?MediusAccountID=7&MediusAccountName=dyllan&applicationID=20231");
        Assert.Equal("xml", root.Name.LocalName.ToLowerInvariant());   // the client compares without case
        XElement first = root.Elements().First();
        Assert.Equal("7", first.Element("accountID")!.Value);
        Assert.Equal("acct_login_success", first.Element("status")!.Element("message")!.Value);
    }

    [Fact]
    public void Career_stats_are_stat_elements_by_svo_name_with_percentiles_per_game_mode()
    {
        MagSvo svo = Service().Svo;
        // 2.12 game type 0 is Sabotage (GameType64)
        XElement sabotage = Get(svo, "/mag/svo/stats/Stats_CareerStatsByID.do?PlayerID=7&gameMode=0").Elements().First();
        Dictionary<string, XElement> stats = sabotage.Elements("stat").ToDictionary(s => s.Attribute("name")!.Value);
        Assert.Equal("5", stats["ENEMIES KILLED"].Value);
        Assert.Equal("50", stats["ENEMIES KILLED"].Attribute("percentile")!.Value);   // above 1 of the 2 soldiers
        Assert.Equal("900", stats["TOTAL EXPERIENCE"].Value);
        Assert.Equal("1", stats["BATTLES COMPLETED"].Value);
        // a number with no game type: every game type
        XElement all = Get(svo, "/mag/svo/stats/Stats_CareerStatsByID.do?PlayerID=7&gameMode=99").Elements().First();
        Assert.Equal("7", all.Elements("stat").Single(s => s.Attribute("name")!.Value == "ENEMIES KILLED").Value);
    }

    [Fact]
    public void Other_paths_are_left_to_the_other_services()
    {
        Assert.Null(Service().Svo.Serve(new MediusWebRequest { Target = "/killzone2/index" }));
    }
}
