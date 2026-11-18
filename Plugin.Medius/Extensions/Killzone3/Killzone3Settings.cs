namespace Plugin.Medius.Extensions.Killzone3;

/// <summary>The <c>ExtensionSettings</c> of a <c>killzone3</c> title.</summary>
public sealed class Killzone3Settings
{
    public Killzone3WebSettings Web { get; set; } = new();
    public Killzone3StatsSettings Stats { get; set; } = new();
}

/// <summary>
/// The web service. Paths are relative to <see cref="RootPath"/>, which must be the path
/// of the universe's SvoUrl (the client appends its own paths to that URL).
/// </summary>
public sealed class Killzone3WebSettings
{
    /// <summary>Host names answered; empty answers any host, below <see cref="RootPath"/> only.</summary>
    public List<string> Hosts { get; set; } = [];

    public string RootPath { get; set; } = "/killzone3/";

    /// <summary>Appended by the client to the base URL (0xedc198) [C].</summary>
    public string LoginPath { get; set; } = "kin/account/nplogin.do";

    /// <summary>URN regionlist, appended by the client to the base URL (0xedc1c0) [C].</summary>
    public string RegionsPath { get; set; } = "kin/stats/region_list.jsp";

    /// <summary>
    /// The login answer's root element, which holds <c>data</c> (the client looks for <c>data</c>
    /// below the root, 0x8b7028) [C; live]. Its name is not checked [C 0x123ad0]; "xml" is a [guess]
    /// at the original.
    /// </summary>
    public string LoginRoot { get; set; } = "xml";

    /// <summary><c>loginresult</c> for a good login: 30000 (0x8b6600) [C].</summary>
    public int SuccessResult { get; set; } = 30000;

    /// <summary><c>loginresult</c> for an unknown player: anything but 30000 is error 20001 [C]; the value is a [guess].</summary>
    public int FailureResult { get; set; } = 30001;

    /// <summary>The regions the region pages list.</summary>
    public List<Killzone3Region> Regions { get; set; } = [];

    /// <summary>The URN store the login hands out.</summary>
    public List<Killzone3Urn> Urns { get; set; } = [];

    /// <summary>
    /// The client's own screen URNs (the screen table at 0xedbb50-0xedbc60: urn:pregame_lobby,
    /// urn:options, urn:intel, urn:quitgame) [C]. Menus open them as pages; a store entry of the
    /// same name replaces the page with web data, and pressing Select in a match crashed the
    /// game on urn:intel [live 2026-11-08]. The login never hands these out.
    /// </summary>
    public List<string> ScreenUrns { get; set; } = ["pregame_lobby", "options", "intel", "quitgame"];

    /// <summary>Game settings sent as the login's <c>client-config</c> (name, value); none by default.</summary>
    public Dictionary<string, string> ClientConfig { get; set; } = [];

    /// <summary>The <c>result</c> text the game URN answers (creategame, finishgame) treat as success: "OK" (0x51e3c8) [C].</summary>
    public string GameResultOk { get; set; } = "OK";

    /// <summary>Directory under the title's medius folder holding URN answer files.</summary>
    public string DataDir { get; set; } = "web";
}

/// <summary>A stat region: <c>id</c> names it (menu_region_&lt;id&gt;), <c>mediuslocation</c> is the Medius location it picks.</summary>
public sealed class Killzone3Region
{
    public int Id { get; set; }
    public int MediusLocation { get; set; }
}

/// <summary>
/// A URN store entry: the page-side name (a page's <c>urn:&lt;name&gt;</c>, without the prefix,
/// as the client's own <c>regionlist</c> entry is named [C]), its path below the session's base,
/// and optionally a file in <see cref="Killzone3WebSettings.DataDir"/> to answer with.
/// </summary>
public sealed class Killzone3Urn
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string File { get; set; } = "";
    /// <summary>A built-in answer instead of <see cref="File"/> (by name in title.json).</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Killzone3UrnAnswer>))]
    public Killzone3UrnAnswer Answer { get; set; }
}

public enum Killzone3UrnAnswer
{
    /// <summary><see cref="Killzone3Urn.File"/>, else an empty document.</summary>
    Data,
    /// <summary><c>result</c> OK and a new <c>svogameid</c> (urn:creategame).</summary>
    CreateGame,
    /// <summary><c>result</c> OK for an end-of-game report (urn:finishgame).</summary>
    FinishGame,
    /// <summary><c>result</c> OK alone.</summary>
    Result,
    /// <summary>One player's stats, by column (urn:playerstats:, field accountID).</summary>
    PlayerStats,
    /// <summary>A leaderboard per requested gameMode (urn:friendsleaderboard:, field gamemode).</summary>
    FriendsLeaderboard,
    /// <summary>The top players (urn:friendtopleaderboard).</summary>
    FriendTopLeaderboard,
}
