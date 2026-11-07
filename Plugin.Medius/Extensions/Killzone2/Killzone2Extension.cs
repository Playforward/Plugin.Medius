using Plugin.Medius.Lobby;
using Plugin.Medius.Web;
using System.Security;
using System.Text;
using System.Text.Json;

namespace Plugin.Medius.Extensions.Killzone2;

/// <summary>
/// Killzone 2 (Guerrilla, PS3, 2009). Stock Medius for the lobby; the online
/// menus are SVML pages from Sony's SVO web service, whose URL the MUIS hands
/// out (the universe's SvoUrl). After the Medius login the game opens that URL
/// as a page (connectuniverse.jsp ConnectSucces, "universe_http"), so the root
/// answers with the URI list the pages name (TicketLoginURI, saverewardsURI,
/// homeURI...). See the title's JOURNAL.md.
/// </summary>
/// <remarks>
/// Formats from the 1.29 EBOOT [C]:
/// <list type="bullet">
/// <item>URI list (URLListTagModule, handler 0x3b0458 / 0x3b0154): a
/// <c>URL_List</c> element inside <c>XML</c> holding <c>DATA</c> elements
/// (dataType URI or DATA, name, value, optional <c>ServerParams</c> of
/// <c>Param</c> name/optional/type) and a <c>BROWSER_INIT</c> element, as PS
/// Rewired's root answers [live 2026-10-31].</item>
/// <item>Login answer (LoginTagModule 0x3b1470): an element <c>SP_Login</c>
/// (or <c>Login</c>) without an <c>action</c> child, with children
/// <c>status</c> (holding <c>id</c>), <c>accountID</c> (optional) and
/// <c>userContext</c>, each a text value (the module's lookups are of child
/// elements, not attributes). 20600 is kept as itself (success); others become
/// -20601 unless in a few failure ranges [C]. PS Rewired answers
/// <c>&lt;XML&gt;&lt;SP_Login&gt;&lt;status&gt;&lt;id&gt;20600&lt;/id&gt;&lt;/status&gt;&lt;userContext&gt;0&lt;/userContext&gt;&lt;/SP_Login&gt;&lt;/XML&gt;</c>
/// as text/xml with an XML declaration, and sets cookies AuthKey and
/// AuthKeyTime [live 2026-10-31]. Attributes instead of children: error 20014.</item>
/// <item>Ticket login (0x3abf74): only to a TicketLoginURI starting with
/// <c>https:</c>; a form with sig (the NP ticket, application/octet-stream),
/// userContext 0, sigclass, siglength, timeZone and languageID. PS Rewired
/// lists it as the hostless <c>https:ticket/login</c>, which passes that check;
/// the client then appends it as it is to the page's directory and POSTs over the
/// page's own connection: /killzone2/https:ticket/login?sig=..&amp;userContext=0&amp;
/// sigclass=0&amp;siglength=16&amp;timeZone=1&amp;languageID=2, the NP ticket as an
/// application/octet-stream body [live 2026-10-31].</item>
/// </list>
/// </remarks>
public partial class Killzone2Extension : IMediusTitleExtension, IMediusWebService
{
    private Killzone2Settings _settings = new();
    private MediusTitle _title = new();
    private MediusContext? _context;
    private string[] _hosts = [];
    // AuthKey cookie -> account id, from the ticket login.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _sessions = new();

    public virtual string Name => "killzone2";

    public virtual void Initialize(MediusTitle title, MediusContext context)
    {
        _title = title;
        _context = context;
        if (title.ExtensionSettings.ValueKind == JsonValueKind.Object)
            _settings = title.ExtensionSettings.Deserialize<Killzone2Settings>(MediusTitle.JsonOptions) ?? new();
        _hosts = [.. _settings.Svo.Hosts.Select(h => Expand(h).ToLowerInvariant())];
        if (context is not null)
        {
            Profiles = new Killzone2Profiles(new AccountDataStore(context.Settings.DataDir), title.Descriptor.TitleId, id => context.Lobby.Accounts.Find(id)?.Name);
            FriendsOf = id => context.Buddies.Get(title.Descriptor.TitleId, id).Buddies;
            Clans = context.Clans;
            Accounts = context.Lobby.Accounts;
        }
        Log.Information("[medius/{Extension}] SVO for {Hosts}: {Count} named URIs at {Base}", Name, string.Join(", ", _hosts),
            _settings.Svo.Uris.Count, Expand(_settings.Svo.BaseUrl));
    }

    public IReadOnlyCollection<string> WebHosts => _hosts;

    public MediusWebResponse? HandleWeb(MediusWebRequest request)
    {
        string? cookie = request.Headers.GetValueOrDefault("cookie");
        string? sentMac = request.Headers.GetValueOrDefault("x-svomac");
        string? scheme = sentMac is null ? null
            : _settings.Svo.Schemes.FirstOrDefault(s => MacInputs(request).Any(fields => SvoMac.Request(request.Target, cookie, fields, s) == sentMac.Trim().ToLowerInvariant()));
        if (sentMac is not null && scheme is null)
            Log.Warning("[medius/{Extension}] {Request}: X-SVOMac {Mac} matches no scheme ({Schemes})", Name, request, sentMac, string.Join(", ", _settings.Svo.Schemes));
        Log.Debug("[medius/{Extension}] {Request} mac {Mac} ({Scheme}) cookie {Cookie} headers {Headers} body {Body}", Name, request, sentMac, scheme,
            cookie, string.Join("; ", request.Headers.Select(h => $"{h.Key}={h.Value}")), Convert.ToHexString(request.Body));

        MediusWebResponse? response = Route(request);
        if (response is null || sentMac is null)
            return response;
        Dictionary<string, string> headers = new(response.Headers) { ["X-SVOMac"] = SvoMac.Response(sentMac.Trim().ToLowerInvariant(), _settings.Svo.ResponseMacKey) };
        return new MediusWebResponse { Status = response.Status, ContentType = response.ContentType, Body = response.Body, Headers = headers, SetCookies = response.SetCookies };
    }

    /// <summary>
    /// What the client may have hashed as the request's form fields: none, the query
    /// (the ticket login sends its fields there, its ticket as the body [live]), or a
    /// text body. See <see cref="SvoMac"/>.
    /// </summary>
    private static IEnumerable<byte[]> MacInputs(MediusWebRequest request)
    {
        yield return [];
        int query = request.Target.IndexOf('?');
        if (request.Method == "POST" && query >= 0)
            yield return Encoding.ASCII.GetBytes(request.Target[(query + 1)..]);
        if (request.Body.Length > 0 && !request.Body.Contains((byte)0))
            yield return request.Body;
    }

    /// <summary>A title's own answers, tried before the SVO service's; null for none.</summary>
    protected virtual MediusWebResponse? RouteTitle(MediusWebRequest request) => null;

    private MediusWebResponse? Route(MediusWebRequest request)
    {
        if (RouteTitle(request) is { } own)
            return own;
        if (request.Path == _settings.Svo.RootPath)
            return _settings.Svo.RootFormat == SvoRootFormat.Svml
                ? new MediusWebResponse { ContentType = PageTypes[0].ContentType, Body = Encoding.UTF8.GetBytes(UriList()) }
                : Xml(UriList());
        if (IsPathOf("TicketLoginURI", request.Path))
            return TicketLogin(request);
        if (IsPathOf("gameCreateURL", request.Path))
            return ScoreGameCreate(request);
        if (IsPathOf("gameJoinURL", request.Path))
            return ScoreGameJoin(request);
        if (IsPathOf("gameSubmitURL", request.Path))
            return ScoreGameSubmit(request);
        if (IsPathOf("createGameSubmitURI", request.Path))
            return CreateGame(request);
        if (IsPathOf("createGameURI", request.Path))
            return string.IsNullOrEmpty(_settings.Svo.Games.CreateFormTemplate)
                ? CreateGame(request)
                : TemplatePage(request, new Killzone2PageSettings { Uri = "createGameURI", Template = _settings.Svo.Games.CreateFormTemplate }, PageTypes[1].ContentType);
        if (IsPathOf("createGamePlayerURI", request.Path) || IsPathOf("createGamePlayerURL", request.Path))
            return CreateGamePlayer(request);
        if (_settings.Svo.Boards.FirstOrDefault(b => IsPathOf(b.Uri, request.Path)) is { } board)
            return Board(request, board);
        if (_settings.Svo.Clans.Uris.Any(u => IsPathOf(u, request.Path)))
            return ClanPage(request);
        if (_settings.Svo.Pages.FirstOrDefault(p => IsPathOf(p.Uri, request.Path)) is { } page)
            return TemplatePage(request, page);
        if (request.Path == _settings.Svo.ProfileUploadPath || _settings.Svo.ProfileUploadPaths.Contains(request.Path))
            SaveProfile(request);
        return Page(request);
    }

    /// <summary>The players' synced profiles; none without a Medius context (tests set one).</summary>
    public Killzone2Profiles? Profiles { get; set; }

    /// <summary>An account's buddies (the MLS's buddy lists); none without a Medius context (tests set one).</summary>
    public Func<int, IEnumerable<int>>? FriendsOf { get; set; }

    /// <summary>
    /// The profile the sync page (svo/killzone2/profile/sync.svml) uploads: its
    /// query is the game's MPScore and ribbon amounts. Stored, then the upload's
    /// own page (which closes the popup) is answered.
    /// </summary>
    private void SaveProfile(MediusWebRequest request)
    {
        if (AccountOf(request) is not { } account || Profiles is null)
        {
            Log.Warning("[medius/{Extension}] {Request}: profile upload without a session", Name, request);
            return;
        }
        if (Profiles.Save(account, request.Query) is { } profile)
            Log.Information("[medius/{Extension}] {Request}: profile of #{Account}: MPScore {Score}, {Ribbons} ribbons", Name, request, account,
                profile.MPScore, profile.Ribbons.Count);
        else
            Log.Warning("[medius/{Extension}] {Request}: profile upload of #{Account} has no {Field}", Name, request, account, Killzone2Profiles.ScoreField);
    }

    /// <summary>
    /// A ranking page (leaderboardURI, leaderboardClanURI, clanUniverseURI...): the
    /// board's template from the title's templates directory, with {BoardTitle},
    /// {BoardDescription} and {BoardEmpty} filled in as text and {BoardRows} as a
    /// Lua table of rows <c>{ pos = 1, name = "..", score = 0, rank = 1, me = 1 }</c>
    /// (rank = 1: the score is an online score the page shows a rank for; me = 1:
    /// the requesting player), plus the page placeholders.
    /// </summary>
    private MediusWebResponse? Board(MediusWebRequest request, Killzone2BoardSettings board)
    {
        if (TemplateFile(board.Template) is not { } file)
        {
            Log.Warning("[medius/{Extension}] {Request}: no template {Template} for board {Board}", Name, request, board.Template, board.Uri);
            return null;
        }
        int? me = AccountOf(request);
        string rows = BoardRows(board, me);
        string text = File.ReadAllText(file)
            .Replace("{BoardTitle}", Attr(board.Title), StringComparison.Ordinal)
            .Replace("{BoardDescription}", Attr(board.Description), StringComparison.Ordinal)
            .Replace("{BoardEmpty}", Attr(board.Empty), StringComparison.Ordinal)
            .Replace("{BoardRows}", rows, StringComparison.Ordinal);
        Log.Debug("[medius/{Extension}] {Request}: board {Board} ({Source}) from {File}", Name, request, board.Uri, board.Source, file);
        return new MediusWebResponse { ContentType = PageTypes[0].ContentType, Body = Encoding.UTF8.GetBytes(FillPage(text, me)) };
    }

    /// <summary>
    /// A page drawn from a template with the page's values: <c>{Name}</c> as
    /// text, <c>{Name.lua}</c> as a Lua string literal (see <see cref="LuaString"/>),
    /// plus the page placeholders. One template serves several menu entries
    /// (friendsURI and mailboxURI share friends.svml).
    /// </summary>
    private MediusWebResponse? TemplatePage(MediusWebRequest request, Killzone2PageSettings page, string? contentType = null)
    {
        if (TemplateFile(page.Template) is not { } file)
        {
            Log.Warning("[medius/{Extension}] {Request}: no template {Template} for page {Page}", Name, request, page.Template, page.Uri);
            return null;
        }
        Log.Debug("[medius/{Extension}] {Request}: page {Page} from {File}", Name, request, page.Uri, file);
        return new MediusWebResponse
        {
            ContentType = contentType ?? (string.IsNullOrEmpty(page.ContentType) ? PageTypes[0].ContentType : page.ContentType),
            Body = Encoding.UTF8.GetBytes(FillPage(FillQuery(FillValues(File.ReadAllText(file), page.Values), request), AccountOf(request))),
        };
    }

    /// <summary>Fills <c>{Query.Name}</c> with the request's query value (empty when absent), as text.</summary>
    public static string FillQuery(string text, MediusWebRequest request) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\{Query\.([A-Za-z0-9_]+)\}", m => Attr(request.QueryString(m.Groups[1].Value) ?? ""));

    /// <summary>Fills <c>{Name}</c> (text) and <c>{Name.lua}</c> (Lua string) for each value.</summary>
    public static string FillValues(string text, IReadOnlyDictionary<string, string> values)
    {
        foreach ((string name, string value) in values)
            text = text.Replace("{" + name + ".lua}", LuaString(value), StringComparison.Ordinal)
                       .Replace("{" + name + "}", Attr(value), StringComparison.Ordinal);
        return text;
    }

    /// <summary>The board's rows as a Lua table constructor (see <see cref="Board"/>).</summary>
    public string BoardRows(Killzone2BoardSettings board, int? me)
    {
        StringBuilder lua = new("{ ");
        int position;
        switch (board.Source)
        {
            case Killzone2BoardSource.Players:
                position = 0;
                foreach ((int accountId, string name, Killzone2Profile profile) in (Profiles?.Ranked() ?? []).Take(board.MaxRows))
                    lua.Append($"{{ pos = {++position}, name = {LuaString(name)}, score = {profile.MPScore}, rank = 1, me = {(accountId == me ? 1 : 0)} }}, ");
                break;
            case Killzone2BoardSource.Friends:
                // The player and their buddies.
                position = 0;
                IEnumerable<int> circle = me is { } self ? [self, .. FriendsOf?.Invoke(self) ?? []] : [];
                foreach ((int accountId, string name, Killzone2Profile profile) in (Profiles?.Ranked(circle) ?? []).Take(board.MaxRows))
                    lua.Append($"{{ pos = {++position}, name = {LuaString(name)}, score = {profile.MPScore}, rank = 1, me = {(accountId == me ? 1 : 0)} }}, ");
                break;
            case Killzone2BoardSource.Clans:
                ClanRows(lua, board, me);
                break;
            case Killzone2BoardSource.ClanMembers:
                // The player's clan, by member score.
                position = 0;
                IEnumerable<int> members = me is { } member && Clans?.OfMember(_title.Descriptor.TitleId, member) is { } myClan ? myClan.Members : [];
                foreach ((int accountId, string name, Killzone2Profile profile) in (Profiles?.Ranked(members) ?? []).Take(board.MaxRows))
                    lua.Append($"{{ pos = {++position}, name = {LuaString(name)}, score = {profile.MPScore}, rank = 1, me = {(accountId == me ? 1 : 0)} }}, ");
                break;
        }
        return lua.Append('}').ToString();
    }

    /// <summary>
    /// A Lua string literal: letters, digits and a few marks as they are, every
    /// other byte as a \ddd escape, so nothing can end the string or the page's
    /// CDATA section.
    /// </summary>
    public static string LuaString(string text)
    {
        StringBuilder lua = new("\"");
        foreach (byte b in Encoding.UTF8.GetBytes(text))
            lua.Append(char.IsAsciiLetterOrDigit((char)b) || b is (byte)' ' or (byte)'-' or (byte)'_' or (byte)'.' ? ((char)b).ToString() : $"\\{b:D3}");
        return lua.Append('"').ToString();
    }

    private string? TemplateFile(string template)
    {
        if (string.IsNullOrEmpty(_settings.Svo.TemplatesDir) || string.IsNullOrEmpty(_title.Descriptor.ResourceDir) || template.Length == 0)
            return null;
        string root = Path.GetFullPath(Path.Combine(_title.Descriptor.ResourceDir, "medius", _settings.Svo.TemplatesDir));
        string file = Path.GetFullPath(Path.Combine(root, template));
        return file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(file) ? file : null;
    }

    /// <summary>The games the SVO has given ids to.</summary>
    public SvoGames Games { get; } = new();

    /// <summary>
    /// The game's score processor (SVOScoreProcessor, update 0x2c43d0) opens its
    /// calls as a popup page (0x489970(url, 1)) and waits for the page to call
    /// back through the Lua global gScoreProcessor [C]: after gameCreateURL or
    /// gameJoinURL it waits up to 25 s for SetSVOGameID(id) (binding 0x2e0e20
    /// stores it at +0x1c; a negative id is an error), else startgame.jsp shows
    /// "Statistics for this game will NOT be logged" [C; live 2026-10-31, when the
    /// answer was the XML below]. After gameSubmitURL it waits up to 25 s for
    /// Close() (0x2e0e10 sets +0x15) [C].
    /// </summary>
    private MediusWebResponse ScoreGameCreate(MediusWebRequest request)
    {
        string name = request.QueryString("gameName") ?? "";
        int host = (int)(request.QueryInt64("hostPlayerList") ?? AccountOf(request) ?? 0);
        if (name.Length == 0 || request.QueryInt64("gameMode") is not { } mode)
        {
            Log.Warning("[medius/{Extension}] {Request}: no game name or mode", Name, request);
            return ScorePage("gamecreate", "gScoreProcessor:SetSVOGameID(-1)");
        }
        SvoGame game = Games.Create(name, host, (int)mode, (int)(request.QueryInt64("timePerDiv") ?? 0), (int)(request.QueryInt64("numDivs") ?? 0));
        Log.Information("[medius/{Extension}] {Request}: SVO game {Id} \"{Name}\" (mode {Mode}, host #{Host})", Name, request, game.Id, game.Name, game.GameMode, host);
        return ScorePage("gamecreate", $"gScoreProcessor:SetSVOGameID({game.Id})");
    }

    /// <summary>A player joining a registered match (gameJoinURL, "SVOGameID=%d" at 0xbfdac8) [C]; answered with its id.</summary>
    private MediusWebResponse ScoreGameJoin(MediusWebRequest request)
    {
        SvoGame? game = request.QueryInt64("SVOGameID") is { } id ? Games.Find((int)id) : null;
        if (game is null)
        {
            Log.Warning("[medius/{Extension}] {Request}: no such SVO game", Name, request);
            return ScorePage("gamejoin", "gScoreProcessor:SetSVOGameID(-1)");
        }
        if (AccountOf(request) is { } account)
            game.Players[account] = 0;
        Log.Information("[medius/{Extension}] {Request}: #{Account} joined SVO game {Id}", Name, request, AccountOf(request), game.Id);
        return ScorePage("gamejoin", $"gScoreProcessor:SetSVOGameID({game.Id})");
    }

    /// <summary>
    /// A match's result (gameSubmitURL, "SVOGameID=%d&amp;endStatus=%d&amp;gameMode=%d%s"
    /// at 0xbfda98, the %s more fields) [C]. Each player sends their own; its
    /// <see cref="SubmitPointsField"/> is the score the match added to their profile
    /// [live 2026-11-01: pts=6 after a win, the next profile upload MPScore 0 -> 6], so it
    /// is added to the stored score at once and the leaderboards show it. The other
    /// fields (win, los, ki, de, ...) are logged.
    /// </summary>
    private MediusWebResponse ScoreGameSubmit(MediusWebRequest request)
    {
        SvoGame? game = request.QueryInt64("SVOGameID") is { } id ? Games.Find((int)id) : null;
        int? account = AccountOf(request);
        Log.Information("[medius/{Extension}] {Request}: result of SVO game {Id} from #{Account}: {Fields}", Name, request, game?.Id,
            account, string.Join("&", request.Query.Select(f => $"{f.Key}={f.Value}")));
        if (game is not null && account is { } player && request.QueryInt64(SubmitPointsField) is { } points && Profiles is not null)
        {
            if (game.Results.TryAdd(player, (int)points))
            {
                Killzone2Profile profile = Profiles.AddScore(player, (int)points);
                Log.Information("[medius/{Extension}] #{Account}: {Points} points from SVO game {Id}, score now {Score}", Name, player, points, game.Id, profile.MPScore);
            }
            else
                Log.Debug("[medius/{Extension}] #{Account}: SVO game {Id} already counted", Name, player, game.Id);
        }
        return ScorePage("gamesubmit", "gScoreProcessor:Close()");
    }

    /// <summary>The gameSubmitURL field holding the points the match gave the player.</summary>
    public const string SubmitPointsField = "pts";

    /// <summary>A popup page whose script calls the score processor, then closes the popup.</summary>
    public static MediusWebResponse ScorePage(string pageId, string call) => new()
    {
        ContentType = "text/svml;charset=UTF-8",
        Body = Encoding.UTF8.GetBytes(
            $"<SVML>\n   <PAGEID name=\"{Attr(pageId)}\" />\n   <_s name=\"scorescript\"><![CDATA[\n      function OnEnterPage()\n         {call}\n         gBrowser:ClosePopup()\n      end\n   ]]></_s>\n</SVML>\n"),
    };

    /// <summary>
    /// svoclient's own game registration (createGameURI, GameCreator.cpp), read by
    /// CreateGameTagModule; KZ2's score processor does not use it. Registers a match: gameName, scertGameID, hostPlayerList (the host's
    /// account id), remotePlayerList, timePerDiv, numDivs, gameMode, from the
    /// format at 1.29 0xbedad8 (file offset) [C; live]. Answered with a
    /// Create_Game element (CreateGameTagModule 0x3beb84): a status child whose
    /// message child is one of the module's result names (table 0xe608d8,
    /// index 0 GAME_CREATE_SUCCESS), and a gameID child, read with atoi [C].
    /// </summary>
    private MediusWebResponse CreateGame(MediusWebRequest request)
    {
        string name = request.QueryString("gameName") ?? "";
        int host = (int)(request.QueryInt64("hostPlayerList") ?? AccountOf(request) ?? 0);
        if (name.Length == 0)
            return GameDocument(CreateGameElement("GAME_NAME_INVALID", 0));
        if (request.QueryInt64("gameMode") is not { } mode)
            return GameDocument(CreateGameElement("GAME_MODE_INVALID", 0));
        SvoGame game = Games.Create(name, host, (int)mode, (int)(request.QueryInt64("timePerDiv") ?? 0), (int)(request.QueryInt64("numDivs") ?? 0));
        Log.Information("[medius/{Extension}] {Request}: SVO game {Id} \"{Name}\" (mode {Mode}, host #{Host})", Name, request, game.Id, game.Name, game.GameMode, host);
        return GameDocument(CreateGameElement("GAME_CREATE_SUCCESS", game.Id));
    }

    /// <summary>
    /// A player of a registered match: SVOGameID and playerSide. Answered with a
    /// GamePlayer element whose status child holds an id child; the client takes
    /// 20422 as success and anything else as failure (0x3beb84) [C].
    /// </summary>
    private MediusWebResponse CreateGamePlayer(MediusWebRequest request)
    {
        SvoGame? game = request.QueryInt64("SVOGameID") is { } id ? Games.Find((int)id) : null;
        int? account = AccountOf(request);
        if (game is null || account is null)
        {
            Log.Warning("[medius/{Extension}] {Request}: no such SVO game, or no session", Name, request);
            return GameDocument(GamePlayerElement(_settings.Svo.Games.GamePlayerFailureId));
        }
        game.Players[account.Value] = (int)(request.QueryInt64("playerSide") ?? 0);
        Log.Information("[medius/{Extension}] {Request}: #{Account} plays SVO game {Id} on side {Side}", Name, request, account, game.Id, game.Players[account.Value]);
        return GameDocument(GamePlayerElement(_settings.Svo.Games.GamePlayerSuccessId));
    }

    public static string CreateGameElement(string result, int gameId) =>
        $"<Create_Game><status><message>{Attr(result)}</message></status><gameID>{gameId}</gameID></Create_Game>";

    public static string GamePlayerElement(int id) => $"<GamePlayer><status><id>{id}</id></status></GamePlayer>";

    /// <summary>A game-service answer: the element in an XML document, as the login's.</summary>
    private MediusWebResponse GameDocument(string element) => LoginAnswer(element);

    /// <summary>
    /// A page or data document from the title's page directory: a request for
    /// /killzone2/home is answered with &lt;ResourceDir&gt;/medius/&lt;PagesDir&gt;/killzone2/home.svml
    /// (text/svml) or .xml (text/xml), read on every request so an edit shows on the next.
    /// {PlayerName}, {AccountId}, {PublicHost} and {BaseUrl} in it are filled in.
    /// </summary>
    private MediusWebResponse? Page(MediusWebRequest request)
    {
        if (string.IsNullOrEmpty(_settings.Svo.PagesDir) || string.IsNullOrEmpty(_title.Descriptor.ResourceDir))
            return null;
        string relative = request.Path.Trim('/');
        if (relative.Length == 0 || relative.Split('/').Any(part => part is "" or "." or ".." || part.Contains('\\')))
            return null;
        string root = Path.GetFullPath(Path.Combine(_title.Descriptor.ResourceDir, "medius", _settings.Svo.PagesDir));
        foreach ((string extension, string contentType) in PageTypes)
        {
            string file = Path.GetFullPath(Path.Combine(root, relative + extension));
            if (!file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(file))
                continue;
            int? accountId = AccountOf(request);
            Log.Debug("[medius/{Extension}] {Request}: page {File} for {Player}", Name, request, file, NameOf(accountId) ?? "(no session)");
            return new MediusWebResponse { ContentType = contentType, Body = Encoding.UTF8.GetBytes(FillPage(File.ReadAllText(file), accountId)) };
        }
        Log.Warning("[medius/{Extension}] {Request}: no page for {Path} yet (looked in {Root} for .svml/.xml)", Name, request, relative, root);
        return null;
    }

    /// <summary>{PlayerName}, {AccountId}, {BaseUrl} and {PublicHost} filled in.</summary>
    private string FillPage(string text, int? accountId) => FillClan(FillProfile(text, accountId is { } id ? Profiles?.Find(id) : null),
            accountId is { } member ? Clans?.OfMember(_title.Descriptor.TitleId, member) : null)
        .Replace("{TimeZones}", TimeZonesLua(), StringComparison.Ordinal)
        .Replace("{PlayerName}", SecurityElement.Escape(NameOf(accountId) ?? ""), StringComparison.Ordinal)
        .Replace("{AccountId}", accountId?.ToString() ?? "0", StringComparison.Ordinal)
        .Replace("{BaseUrl}", Expand(_settings.Svo.BaseUrl), StringComparison.Ordinal)
        .Replace(MediusUniverse.PublicHostPlaceholder, _context?.PublicAddress.ToString() ?? "", StringComparison.Ordinal);

    /// <summary>
    /// What a page pushes into the player's profile: {ProfileSyncScore}, the higher of the stored score and
    /// MinScore (0 without a profile), and {ProfileSyncRank}, MinRank (-1 for none).
    /// </summary>
    public static string FillProfile(string text, Killzone2Profile? profile) => text
        .Replace("{ProfileSyncScore}", Math.Max(profile?.MPScore ?? 0, profile?.MinScore ?? 0).ToString(), StringComparison.Ordinal)
        .Replace("{ProfileSyncRank}", (profile?.MinRank ?? -1).ToString(), StringComparison.Ordinal);

    /// <summary>
    /// {TimeZones}: the distinct UTC offsets of the server's time zone database, west to east, as a Lua
    /// list of strings ("UTC-05:00"...). The profile keeps the chosen one (SetTimeZone, a 50-byte string at
    /// +0xafc, 1.29 0x82e40) [C].
    /// </summary>
    public static string TimeZonesLua() =>
        "{ " + string.Concat(TimeZoneInfo.GetSystemTimeZones().Select(z => z.BaseUtcOffset).Append(TimeSpan.Zero).Distinct().Order()
            .Select(o => LuaString($"UTC{(o < TimeSpan.Zero ? "-" : "+")}{o:hh\\:mm}") + ", ")) + "}";

    private string? NameOf(int? accountId) => accountId is { } id && _context is not null ? _context.Lobby.Accounts.Find(id)?.Name : null;

    // PS Rewired answers pages as text/svml and data documents as text/xml, both ;charset=UTF-8 [live].
    private static readonly (string Extension, string ContentType)[] PageTypes =
        [(".svml", "text/svml;charset=UTF-8"), (".xml", "text/xml;charset=UTF-8")];

    /// <summary>The root document: every configured URI by name (URLListTagModule).</summary>
    public string UriList()
    {
        bool svml = _settings.Svo.RootFormat == SvoRootFormat.Svml;
        StringBuilder xml = new(svml ? "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<SVML>\n<SET name=\"IP\" IPAddress=\"0.0.0.0\" />\n" : "<XML><URL_List>");
        foreach (Killzone2SvoUri uri in _settings.Svo.Uris)
        {
            xml.Append($"<DATA dataType=\"{Attr(uri.DataType)}\" name=\"{Attr(uri.Name)}\" value=\"{Attr(UrlOf(uri.Value))}\"");
            if (uri.Params.Count == 0)
            {
                xml.Append("/>");
                continue;
            }
            xml.Append("><ServerParams>");
            foreach (Killzone2SvoParam param in uri.Params)
                xml.Append($"<Param name=\"{Attr(param.Name)}\" optional=\"{(param.Optional ? "true" : "false")}\" type=\"{Attr(param.Type)}\"/>");
            xml.Append("</ServerParams></DATA>");
        }
        if (!string.IsNullOrEmpty(_settings.Svo.BrowserInit))
            xml.Append($"<BROWSER_INIT name=\"{Attr(_settings.Svo.BrowserInit)}\"/>");
        return xml.Append(svml ? "\n</SVML>" : "</URL_List></XML>").ToString();
    }

    /// <summary>
    /// The NP ticket arrives as the form's sig field; the player is the account
    /// the Medius login made for its online id.
    /// </summary>
    private MediusWebResponse TicketLogin(MediusWebRequest request)
    {
        NpTicket? ticket = FindTicket(request.Body);
        MediusAccount? account = ticket is null || _context is null ? null : _context.Lobby.Accounts.Find(_title.Descriptor.TitleId, ticket.OnlineId);
        if (account is null)
        {
            Log.Warning("[medius/{Extension}] {Request}: ticket login for {OnlineId}, no such account", Name, request, ticket?.OnlineId ?? "(no ticket)");
            return LoginAnswer(LoginElement(0, _settings.Svo.Login.FailureStatus));
        }
        string authKey = StartSession(account.AccountId);
        Log.Information("[medius/{Extension}] {Request}: SVO login {Account} #{Id}", Name, request, account.Name, account.AccountId);
        MediusWebResponse answer = LoginAnswer(LoginElement(account.AccountId, _settings.Svo.Login.SuccessStatus));
        return new MediusWebResponse
        {
            ContentType = answer.ContentType,
            Body = answer.Body,
            SetCookies = [.. (_settings.Svo.Login.AccountCookies
                              ? new[] { $"AcctID={account.AccountId}; path=/", $"AcctName={account.Name}; path=/",
                                        $"X-SVOTitleID={_title.AppIds.FirstOrDefault()}; path=/" }
                              : []),
                          $"{_settings.Svo.Login.AuthCookie}={authKey}; path=/",
                          $"{_settings.Svo.Login.AuthTimeCookie}={Uri.EscapeDataString(DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"))}; path=/"],
        };
    }

    public string LoginElement(int accountId, int status)
    {
        Killzone2LoginSettings login = _settings.Svo.Login;
        string account = login.IncludeAccountId ? $"<accountID>{accountId}</accountID>" : "";
        return $"<{login.Element}><status><id>{status}</id></status>{account}<userContext>{login.UserContext}</userContext></{login.Element}>";
    }

    /// <summary>A new AuthKey for the account (the cookie the ticket login sets).</summary>
    public string StartSession(int accountId)
    {
        string authKey = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        _sessions[authKey] = accountId;
        return authKey;
    }

    /// <summary>The account an SVO request's AuthKey cookie names (set at the ticket login); null when none.</summary>
    public int? AccountOf(MediusWebRequest request)
    {
        string? cookies = request.Headers.GetValueOrDefault("cookie");
        if (cookies is null)
            return null;
        foreach (string part in cookies.Split(';'))
        {
            string[] pair = part.Trim().Split('=', 2);
            if (pair.Length == 2 && pair[0] == _settings.Svo.Login.AuthCookie && _sessions.TryGetValue(pair[1], out int account))
                return account;
        }
        return null;
    }

    /// <summary>The first NP ticket in the body (the sig part of the form, wherever the encoding put it).</summary>
    private static NpTicket? FindTicket(byte[] body)
    {
        for (int i = 0; i + 8 <= body.Length; i++)
        {
            if (body[i] != 0x21 && body[i] != 0x31 && body[i] != 0x41)
                continue;
            if (NpTicket.Parse(body.AsSpan(i)) is { OnlineId.Length: > 0 } ticket)
                return ticket;
        }
        return null;
    }

    /// <summary>
    /// The path a named URI is requested at. A hostless value such as
    /// <c>https:ticket/login</c> is requested under the page's directory with the
    /// value appended as it is; its relative part alone is accepted too.
    /// </summary>
    private bool IsPathOf(string name, string requestPath)
    {
        if (_settings.Svo.Uris.FirstOrDefault(u => u.Name == name) is not { } uri)
            return false;
        string url = UrlOf(uri.Value);
        if (url.Contains("://"))
            return new Uri(url).AbsolutePath == requestPath;
        // KZ2 appends the value as it is to the page's directory:
        // /killzone2/ + https:ticket/login [live 2026-10-31].
        string relative = url[(url.IndexOf(':') + 1)..].TrimStart('/');
        return requestPath.EndsWith("/" + url, StringComparison.Ordinal) || requestPath.EndsWith("/" + relative, StringComparison.Ordinal);
    }

    /// <summary>An absolute URL as is; a scheme with no host (<c>https:ticket/login</c>) as is; a path under <see cref="Killzone2SvoSettings.BaseUrl"/>.</summary>
    private string UrlOf(string value) =>
        value.Contains("://") || (value.IndexOf(':') is > 0 and var colon && value[..colon].All(char.IsAsciiLetter)) ? Expand(value)
        : Expand(_settings.Svo.BaseUrl).TrimEnd('/') + value;

    private static string Attr(string text) => SecurityElement.Escape(text);

    private string Expand(string text) =>
        _context is null ? text : text.Replace(MediusUniverse.PublicHostPlaceholder, _context.PublicAddress.ToString(), StringComparison.Ordinal);

    private static MediusWebResponse Xml(string body) => new() { ContentType = "text/xml", Body = Encoding.UTF8.GetBytes(body) };

    /// <summary>The login element in the configured document (root element and content type).</summary>
    private MediusWebResponse LoginAnswer(string element) => new()
    {
        ContentType = _settings.Svo.Login.ContentType,
        Body = Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<{_settings.Svo.Login.Root}>{element}</{_settings.Svo.Login.Root}>"),
    };
}

public sealed class Killzone2Settings
{
    public Killzone2SvoSettings Svo { get; set; } = new();
}

/// <summary>The SVO web service (see <see cref="Killzone2Extension"/>).</summary>
public sealed class Killzone2SvoSettings
{
    /// <summary>Host names (Host header) the service answers for; <c>{PublicHost}</c> allowed.</summary>
    public List<string> Hosts { get; set; } = [];

    /// <summary>Where relative URIs point (scheme, host, port); <c>{PublicHost}</c> allowed.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Directory under the title's medius folder that pages are served from (see Killzone2Extension.Page); none when empty.</summary>
    public string PagesDir { get; set; } = "";

    /// <summary>The path the universe's SvoUrl names; answered with the URI list.</summary>
    public string RootPath { get; set; } = "/";

    /// <summary>The URI list the root answers with, in order.</summary>
    public List<Killzone2SvoUri> Uris { get; set; } = [];

    /// <summary>How the root document is laid out (see <see cref="Killzone2Extension.UriList"/>).</summary>
    public SvoRootFormat RootFormat { get; set; } = SvoRootFormat.UrlList;

    /// <summary>Name of the BROWSER_INIT element after the list ("init" on PS Rewired); none when empty.</summary>
    public string BrowserInit { get; set; } = "";

    /// <summary>Scheme strings tried when checking a request's X-SVOMac (the client mixes in its connection's).</summary>
    public List<string> Schemes { get; set; } = ["http://", "https://"];

    /// <summary>What the client mixes into the response MAC after the request's (1.29: 0xc48708).</summary>
    public string ResponseMacKey { get; set; } = "";

    public Killzone2LoginSettings Login { get; set; } = new();

    public Killzone2GameSettings Games { get; set; } = new();

    /// <summary>Directory under the title's medius folder holding page templates (boards); none when empty.</summary>
    public string TemplatesDir { get; set; } = "";

    /// <summary>The ranking pages, each answering a named URI from a template.</summary>
    public List<Killzone2BoardSettings> Boards { get; set; } = [];

    /// <summary>The clan page (create, my clan, invitations; see Killzone2Extension.ClanPage).</summary>
    public Killzone2ClanSettings Clans { get; set; } = new();

    /// <summary>Pages drawn from a template with fixed values, each answering a named URI.</summary>
    public List<Killzone2PageSettings> Pages { get; set; } = [];

    /// <summary>Where the sync page uploads the profile (its query); stored before the page there is answered. None when empty.</summary>
    public string ProfileUploadPath { get; set; } = "";

    /// <summary>More paths whose query is stored as the profile before their page is answered (the home page's Continue).</summary>
    public List<string> ProfileUploadPaths { get; set; } = [];
}

/// <summary>A ranking page (see Killzone2Extension.Board).</summary>
public sealed class Killzone2BoardSettings
{
    /// <summary>The named URI the menu opens it by (leaderboardURI...).</summary>
    public string Uri { get; set; } = "";
    /// <summary>The template file under TemplatesDir.</summary>
    public string Template { get; set; } = "";
    public Killzone2BoardSource Source { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Shown when there are no rows.</summary>
    public string Empty { get; set; } = "";
    public int MaxRows { get; set; } = 100;
}

/// <summary>A template page (see Killzone2Extension.TemplatePage).</summary>
public sealed class Killzone2PageSettings
{
    /// <summary>The named URI the menu opens it by (friendsURI...).</summary>
    public string Uri { get; set; } = "";
    /// <summary>The template file under TemplatesDir.</summary>
    public string Template { get; set; } = "";
    /// <summary>What the template's {Name} / {Name.lua} placeholders become.</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    /// <summary>The answer's content type; empty is text/svml (pages). Data documents are text/xml;charset=UTF-8.</summary>
    public string ContentType { get; set; } = "";
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Killzone2BoardSource>))]
public enum Killzone2BoardSource
{
    /// <summary>Players with a synced profile, by MPScore.</summary>
    Players,
    /// <summary>Clans, by their members' total MPScore.</summary>
    Clans,
    /// <summary>The members of the requesting player's clan, by MPScore.</summary>
    ClanMembers,
    /// <summary>The requesting player and their buddies, by MPScore.</summary>
    Friends,
    /// <summary>Nothing the server keeps yet (clan tournaments): always empty.</summary>
    None,
}

/// <summary>The SVO game service's answers (CreateGameTagModule, 1.29 0x3beb84).</summary>
public sealed class Killzone2GameSettings
{
    /// <summary>
    /// A template (under TemplatesDir) createGameURI answers with instead of registering the game: a
    /// Create_Game form whose action names the URI the client submits the filled fields to
    /// (Resistance: Fall of Man's PSRewired answers so, action createGameSubmitURI [live]). Empty: the
    /// game is registered at createGameURI (Killzone 2).
    /// </summary>
    public string CreateFormTemplate { get; set; } = "";

    /// <summary>The GamePlayer status id the client takes as success (compared with 0x4fc6) [C].</summary>
    public int GamePlayerSuccessId { get; set; } = 20422;

    /// <summary>A GamePlayer status id for failure; any other value fails [C], this one is a [guess].</summary>
    public int GamePlayerFailureId { get; set; } = 20423;
}

/// <summary>One DATA element of the URI list.</summary>
public sealed class Killzone2SvoUri
{
    /// <summary>The name the pages resolve (homeURI, TicketLoginURI, gameCreateURL...).</summary>
    public string Name { get; set; } = "";
    /// <summary>An absolute URL, a scheme with no host (https:ticket/login), or a path under BaseUrl.</summary>
    public string Value { get; set; } = "";
    /// <summary>URI, or DATA (PS Rewired uses DATA for a few, e.g. gamePostBinaryStatsURI).</summary>
    public string DataType { get; set; } = "URI";
    /// <summary>ServerParams the client must send with it.</summary>
    public List<Killzone2SvoParam> Params { get; set; } = [];
}

public sealed class Killzone2SvoParam
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "integer";
    public bool Optional { get; set; }
}

/// <summary>The answer to the ticket login (LoginTagModule, 1.29 0x3b1470).</summary>
public sealed class Killzone2LoginSettings
{
    /// <summary>The answer's root element and content type: XML / text/xml, as PS Rewired's [live].</summary>
    public string Root { get; set; } = "XML";
    public string ContentType { get; set; } = "text/xml";

    /// <summary>"SP_Login" (type 1) or "Login" (type 2).</summary>
    public string Element { get; set; } = "SP_Login";
    public int SuccessStatus { get; set; } = 20600;
    public int FailureStatus { get; set; } = -20601;
    public int UserContext { get; set; }
    /// <summary>Send an accountID child (the module reads one if present [C]; PS Rewired sends none [live]).</summary>
    public bool IncludeAccountId { get; set; }
    /// <summary>Cookies set at login, as PS Rewired's: the session key and the login time.</summary>
    public string AuthCookie { get; set; } = "AuthKey";
    public string AuthTimeCookie { get; set; } = "AuthKeyTime";
    /// <summary>
    /// Also set AcctID, AcctName and X-SVOTitleID (the title's first app id), which Resistance: Fall
    /// of Man's client sends back on every later request, as PSRewired sets them [live].
    /// </summary>
    public bool AccountCookies { get; set; }
}

/// <summary>The root document's layout.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<SvoRootFormat>))]
public enum SvoRootFormat
{
    /// <summary>XML / URL_List holding the DATA elements, as text/xml (Killzone 2, URLListTagModule [C]).</summary>
    UrlList,
    /// <summary>
    /// SVML: SET name="IP", the DATA elements and BROWSER_INIT directly under the root, as
    /// text/svml (Resistance: Fall of Man on PSRewired [live]).
    /// </summary>
    Svml,
}
