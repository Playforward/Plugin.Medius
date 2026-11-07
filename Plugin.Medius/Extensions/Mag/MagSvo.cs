using System.Security;
using System.Text;
using Plugin.Medius.Extensions.Killzone2;
using Plugin.Medius.Web;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// MAG's SVO service: the stats pages the 2.12 client reads over HTTP
/// (scertHttpClient 3.04.51) from its universe's SvoUrl, joined to each page
/// as "%s/%s". Formats from the 2.12 mage_g [C]:
/// <list type="bullet">
/// <item>Login (NetStatsQueryMgr, request 0x11e43c): account/SP_Login_Submit_MAG.do
/// with MediusAccountID, MediusAccountName and applicationID. The answer
/// (0x11fc28) is a document whose root is named "xml" (compared without case,
/// 0xaf21a8), whose first element holds an <c>accountID</c> (decimal) and a
/// <c>status</c> with a <c>message</c> of <c>acct_login_success</c>.</item>
/// <item>Career stats (MagAccountStatsQuery, URL 0x72ea74, query 0x72e850):
/// stats/Stats_CareerStatsByID.do?PlayerID=&lt;account&gt;&amp;gameMode=&lt;n&gt;
/// (Monthly and Weekly are the same with other pages). The answer (0x72d9bc):
/// a root, an element in it, and in that one <c>stat</c> element per stat with
/// a <c>name</c> attribute (the stat's SVO name, character_stats.zpr's SVO,
/// which 0x726448 compares with the string each loaded stat entry holds; id
/// below 138), an optional <c>percentile</c> attribute and the value as
/// decimal text; the names "GAME POINTS" and "OVERALL POINTS" are skipped.</item>
/// <item>The awards, weapons and trends blobs
/// (stats/Stats_BinaryStatsDownload_Submit.do: accountID, gameMode, blobType)
/// aren't served yet.</item>
/// </list>
/// The response MAC is the SVO client's (<see cref="SvoMac"/>; the 2.12 key
/// is the same string as Killzone 2's, at 0x12681a8).
/// </summary>
public sealed class MagSvo(MagSettings settings, Func<int, MagCharacter?> characterOf, Func<IEnumerable<MagCharacter>> characters,
    Func<int, string?> accountName)
{
    public const string LoginPage = "account/SP_Login_Submit_MAG.do";
    public const string CareerPage = "stats/Stats_CareerStatsByID.do";
    public const string MonthlyPage = "stats/Stats_MonthlyStatsByID.do";
    public const string WeeklyPage = "stats/Stats_WeeklyStatsByID.do";

    /// <summary>The SVO client's response MAC key (2.12 0x12681a8).</summary>
    public const string ResponseMacKey = "sp9ck0348sld00000000000000000000";

    /// <summary>The answer for a request under <see cref="MagSettings.SvoPath"/>; null for any other path.</summary>
    public MediusWebResponse? Serve(MediusWebRequest request)
    {
        string root = settings.SvoPath.TrimEnd('/') + "/";
        if (!request.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return null;
        string page = request.Path[root.Length..];
        MediusWebResponse? response = page switch
        {
            LoginPage => Login(request),
            CareerPage or MonthlyPage or WeeklyPage => Stats(request, page),
            _ => null,
        };
        Log.Information("[medius/mag] SVO {Request} from {Remote}: {Result}", request, request.Remote,
            response is null ? "not served" : $"{response.Body.Length} bytes");
        if (response is null || request.Headers.GetValueOrDefault("x-svomac") is not { } mac)
            return response;
        Dictionary<string, string> headers = new(response.Headers) { ["X-SVOMac"] = SvoMac.Response(mac.Trim().ToLowerInvariant(), ResponseMacKey) };
        return new MediusWebResponse { Status = response.Status, ContentType = response.ContentType, Body = response.Body, Headers = headers, SetCookies = response.SetCookies };
    }

    /// <summary>The fields of a request: its query, and a form body if it has one.</summary>
    private static Dictionary<string, string> Fields(MediusWebRequest request)
    {
        Dictionary<string, string> fields = new(request.Query, StringComparer.OrdinalIgnoreCase);
        if (request.Body.Length > 0 && !request.Body.Contains((byte)0))
            foreach ((string key, string value) in MediusWebRequest.ParseQuery(Encoding.ASCII.GetString(request.Body)))
                fields[key] = value;
        return fields;
    }

    /// <summary>The SVO login: the account the client names, if the lobby knows it.</summary>
    private MediusWebResponse Login(MediusWebRequest request)
    {
        Dictionary<string, string> fields = Fields(request);
        int accountId = int.TryParse(fields.GetValueOrDefault("MediusAccountID"), out int id) ? id : 0;
        bool known = accountName(accountId) is not null;
        Log.Information("[medius/mag] SVO login of account {Account} ({Name}, application {App}): {Result}", accountId,
            fields.GetValueOrDefault("MediusAccountName"), fields.GetValueOrDefault("applicationID"), known ? "ok" : "unknown account");
        string message = known ? "acct_login_success" : "acct_login_failure";
        return Xml($"<SP_Login><accountID>{accountId}</accountID><status><message>{message}</message></status></SP_Login>");
    }

    /// <summary>
    /// A stats page: the player's career stats for the game mode (the
    /// client's game type number; one it has no game type for gets every game
    /// type), each with its percentile among the lobby's soldiers. The monthly
    /// and weekly pages get the career too, as nothing is kept by period.
    /// </summary>
    private MediusWebResponse Stats(MediusWebRequest request, string page)
    {
        Dictionary<string, string> fields = Fields(request);
        int accountId = int.TryParse(fields.GetValueOrDefault("PlayerID"), out int id) ? id : 0;
        string? gameType = uint.TryParse(fields.GetValueOrDefault("gameMode"), out uint mode) ? settings.Version.GameTypes.NameOf(mode) : null;
        StringBuilder xml = new("<STATS>");
        if (characterOf(accountId) is { } soldier)
        {
            Dictionary<string, long> mine = StatsOf(soldier, gameType);
            List<Dictionary<string, long>> everyone = [.. characters().Select(c => StatsOf(c, gameType))];
            foreach ((string name, MagStatInfo info) in Table.Stats.OrderBy(s => s.Value.Id))
            {
                if (info.Svo is null || !mine.TryGetValue(name, out long value))
                    continue;
                int below = everyone.Count(s => s.GetValueOrDefault(name) < value);
                int percentile = everyone.Count == 0 ? 0 : below * 100 / everyone.Count;
                xml.Append($"<stat name=\"{SecurityElement.Escape(info.Svo)}\" percentile=\"{percentile}\">{value}</stat>");
            }
            Log.Information("[medius/mag] SVO {Page} of {Name} (account {Account}, gameMode {Mode} = {GameType}): {Count} stats",
                page, soldier.Name, accountId, fields.GetValueOrDefault("gameMode"), gameType ?? "all", mine.Count);
        }
        else
            Log.Information("[medius/mag] SVO {Page} for account {Account}: no soldier", page, accountId);
        return Xml(xml.Append("</STATS>").ToString());
    }

    /// <summary>The client's stats, their SVO names and update rules.</summary>
    private MagStatTable Table => MagStatTable.Embedded(settings.Version);

    /// <summary>A soldier's career stats for one game type (or all), with TotalExperience from the soldier.</summary>
    private Dictionary<string, long> StatsOf(MagCharacter soldier, string? gameType)
    {
        Dictionary<string, long> stats;
        lock (soldier)
            stats = gameType is not null
                ? new(soldier.CareerStats.GetValueOrDefault(gameType) ?? [])
                : soldier.CareerStats.Values.SelectMany(c => c.Keys).Distinct().ToDictionary(n => n, n => soldier.CareerStat(n, Table));
        stats["TotalExperience"] = soldier.Experience;
        return stats;
    }

    private static MediusWebResponse Xml(string element) => new()
    {
        ContentType = "text/xml",
        Body = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<XML>{element}</XML>"),
    };
}
