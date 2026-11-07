using Plugin.Medius.Extensions;
using Plugin.Medius.Extensions.Killzone2;
using Plugin.Medius.Extensions.ResistanceFallOfMan;
using Plugin.Medius.Web;
using System.Text;
using System.Text.Json;

namespace Plugin.Medius.Tests;

/// <summary>
/// Resistance: Fall of Man's SVO service (Extensions/ResistanceFallOfMan): the Killzone 2 service
/// with the title's settings, checked against PSRewired's answers (proxy captures 2026-11-01).
/// </summary>
public sealed class ResistanceFallOfManSvoTests
{
    private const string ResponseKey = "sp9ck0348sld00000000000000000000";

    private static ResistanceExtension Extension(string templatesDir = "")
    {
        MediusTitle title = new()
        {
            AppIds = [20174],
            ExtensionSettings = JsonDocument.Parse($$"""
                { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test:10091", "ResponseMacKey": "{{ResponseKey}}",
                           "RootPath": "/resistancefom/index", "RootFormat": "Svml", "BrowserInit": "init",
                           "TemplatesDir": "{{templatesDir}}",
                           "Login": { "Root": "SVML", "ContentType": "text/svml;charset=UTF-8", "IncludeAccountId": true, "AccountCookies": true },
                           "Games": { "CreateFormTemplate": "creategame.xml" },
                           "Uris": [ { "Name": "createGameURI", "Value": "/resistancefom/game/create?gameMode=%d&timePerDiv=%d&numDivs=%d", "DataType": "DATA" },
                                     { "Name": "createGameSubmitURI", "Value": "/resistancefom/game/submit", "DataType": "DATA" },
                                     { "Name": "TicketLoginURI", "Value": "https:ticket/login", "DataType": "DATA" } ] } }
                """).RootElement,
        };
        ResistanceExtension extension = new();
        extension.Initialize(title, null!);
        return extension;
    }

    [Fact]
    public void Registered_under_its_own_name()
    {
        Assert.IsType<ResistanceExtension>(MediusExtensionRegistry.Create("resistance"));
        Assert.IsType<Killzone2Extension>(MediusExtensionRegistry.Create("killzone2"));
    }

    /// <summary>PSRewired's root: SET IP, the DATA entries and BROWSER_INIT straight under SVML, as text/svml [live].</summary>
    [Fact]
    public void Root_is_an_svml_page()
    {
        string root = Extension().UriList();
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SVML>\n<SET name=\"IP\" IPAddress=\"0.0.0.0\" />\n", root);
        Assert.Contains("<DATA dataType=\"DATA\" name=\"createGameURI\" value=\"http://svo.test:10091/resistancefom/game/create?gameMode=%d&amp;timePerDiv=%d&amp;numDivs=%d\"/>", root);
        Assert.EndsWith("<BROWSER_INIT name=\"init\"/>\n</SVML>", root);
        Assert.DoesNotContain("URL_List", root);
    }

    /// <summary>The root request RFOM sends and the MAC PSRewired answered it with [live].</summary>
    [Fact]
    public void Root_answer_carries_psrewireds_mac()
    {
        MediusWebRequest request = new()
        {
            Method = "GET", Target = "/resistancefom/index?languageID=1",
            Headers = new Dictionary<string, string> { ["host"] = "svo.test", ["x-svomac"] = "ff431bfdcd1012c23fecae337b4ba89c" },
        };
        MediusWebResponse response = Extension().HandleWeb(request)!;
        Assert.Equal("text/svml;charset=UTF-8", response.ContentType);
        Assert.Equal("53f47f5604d1ef79e08354f911049719", response.Headers["X-SVOMac"]);
    }

    [Fact]
    public void Ticket_login_mac_is_the_same_scheme()
    {
        // POST /resistancefom/https:ticket/login?... sent a1efb18c..., PSRewired answered dc89b0de... [live].
        const string target = "/resistancefom/https:ticket/login?sig=FT2mx0Gt2EhHNgobZZkLCg%3d%3d&userContext=0&sigclass=0&siglength=16&timeZone=21&languageID=1";
        Assert.Equal("a1efb18cff4f52cb8bd2126dbb313053", SvoMac.Request(target, null, [], "http://"));
        Assert.Equal("dc89b0decec15a4a442866549aa414f5", SvoMac.Response("a1efb18cff4f52cb8bd2126dbb313053", ResponseKey));
    }

    [Fact]
    public void Login_answer_is_svml_with_the_account()
    {
        Assert.Equal("<SP_Login><status><id>20600</id></status><accountID>95792</accountID><userContext>0</userContext></SP_Login>",
            Extension().LoginElement(95792, 20600));
    }

    /// <summary>createGameURI answers PSRewired's form; the client submits the filled fields to createGameSubmitURI [live].</summary>
    [Fact]
    public void Create_game_answers_the_form_template()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rfom-svo-" + Guid.NewGuid().ToString("N"));
        string templates = Path.Combine(dir, "medius", "t");
        Directory.CreateDirectory(templates);
        try
        {
            File.WriteAllText(Path.Combine(templates, "creategame.xml"), "<XML><Create_Game action=\"createGameSubmitURI\"><gameMode name=\"gameMode\" value=\"{Query.gameMode}\"/></Create_Game></XML>");
            MediusTitle title = new()
            {
                AppIds = [20174],
                Descriptor = new ServerShared.Titles.TitleDescriptor { TitleId = "resistance", ResourceDir = dir },
                ExtensionSettings = JsonDocument.Parse("""
                    { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test:10091", "TemplatesDir": "t",
                               "Games": { "CreateFormTemplate": "creategame.xml" },
                               "Uris": [ { "Name": "createGameURI", "Value": "/resistancefom/game/create", "DataType": "DATA" } ] } }
                    """).RootElement,
            };
            ResistanceExtension extension = new();
            extension.Initialize(title, null!);
            MediusWebResponse response = extension.HandleWeb(new MediusWebRequest
            {
                Method = "GET", Target = "/resistancefom/game/create?gameMode=9&timePerDiv=-805240288&numDivs=268706072",
                Headers = new Dictionary<string, string> { ["host"] = "svo.test" },
            })!;
            Assert.Equal("text/xml;charset=UTF-8", response.ContentType);
            Assert.Equal("<XML><Create_Game action=\"createGameSubmitURI\"><gameMode name=\"gameMode\" value=\"9\"/></Create_Game></XML>",
                Encoding.UTF8.GetString(response.Body));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
