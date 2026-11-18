using Plugin.Medius.Lobby;
using Plugin.Medius.Web;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Plugin.Medius.Extensions.Killzone3;

/// <summary>
/// Killzone 3 (Guerrilla, PS3, 2011). Stock Medius (SCE-RT 3.06) for the lobby, plus
/// Guerrilla's own HTTP service in Killzone 2's SVO role. Unlike Killzone 2 the game
/// has no SVO browser: its online menus are GUM pages on the disc (XML + Lua), and the
/// service only answers XML data that the pages bind through <c>urn:</c> names. Static
/// evidence in RE-Work/kz3/NOTES.md (1.14 EBOOT addresses) [C]; nothing here is
/// live-tested yet.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The base URL is a string of the chosen universe's entry (entry +8, login setup
/// 0x8b5854) [C]; that it is the universe's SvoURL is a [guess]. The client appends
/// <c>kin/account/nplogin.do</c> to it, and when it logs in for the region page (mode 2)
/// it registers URN <c>regionlist</c> as base + <c>kin/stats/region_list.jsp</c> [C].</item>
/// <item>The login request (builder 0x8b5498) carries id, sig, sigclass 0, timeZone 1,
/// languageID 2, userContext 0, sig2, version 0x10000 and statRegion, after the Medius
/// login [C]; the NP ticket travels with it (object at request +0x2c) [guess].</item>
/// <item>The answer (0x8b7028 -> 0x8b6600) is a document whose root element has a
/// <c>data</c> child (else error 20002): the parser 0x106a30 returns the first element
/// it builds (0x123ad0), and 0x8b7028 looks for <c>data</c> below it [C; live 2026-11-08:
/// <c>&lt;data&gt;</c> as the root itself gave 20002]. The root's name is
/// <see cref="Killzone3WebSettings.LoginRoot"/>. <c>loginresult</c> 30000 is success (else error 20001); then
/// <c>nonce</c> (kept at 0x1554680 and hashed into every later X-SVOMac),
/// <c>urnstore</c> of <c>urn</c> (<c>name</c>, <c>uri</c>) pairs (added with
/// 0xcb6dd4), and optional <c>personalstats</c>, <c>friendsboard</c>, <c>clan</c> and
/// <c>client-config</c> (each null-checked) [C].</item>
/// </list>
/// Every URN this service hands out points below a per-login path segment
/// (<c>s/&lt;session&gt;/</c>), so later requests name their player without relying on
/// cookies the client may not keep.
/// </remarks>
public sealed class Killzone3Extension : IMediusTitleExtension, IMediusWebService
{
    private const string SessionSegment = "s/";

    private Killzone3WebSettings _settings = new();
    private MediusTitle _title = new();
    private MediusContext? _context;
    private string[] _hosts = [];
    private readonly ConcurrentDictionary<string, Killzone3Session> _sessions = new();
    private readonly ConcurrentDictionary<long, Killzone3Game> _games = new();
    // svogameids are not kept across restarts; starting from the clock keeps them from repeating.
    private long _nextGameId = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public string Name => "killzone3";

    /// <summary>Finds a player's account by online id; the Medius lobby's accounts unless a test sets it.</summary>
    public Func<string, MediusAccount?>? FindAccount { get; set; }

    /// <summary>A player's name by account id; the Medius lobby's accounts unless a test sets it.</summary>
    public Func<int, string?>? NameOf { get; set; }

    /// <summary>A player's Medius buddies, for <see cref="Killzone3LeaderboardPlayers.Buddies"/>.</summary>
    public Func<int, IEnumerable<int>>? BuddiesOf { get; set; }

    /// <summary>The career stats; kept under the server's data directory unless a test sets it.</summary>
    public Killzone3Stats? Stats { get; set; }

    public void Initialize(MediusTitle title, MediusContext context)
    {
        _title = title;
        _context = context;
        Killzone3Settings all = title.ExtensionSettings.ValueKind == JsonValueKind.Object
            ? title.ExtensionSettings.Deserialize<Killzone3Settings>(MediusTitle.JsonOptions) ?? new()
            : new();
        _settings = all.Web;
        _hosts = [.. _settings.Hosts.Select(h => Expand(h).ToLowerInvariant())];
        foreach (Killzone3Urn urn in _settings.Urns.Where(u => _settings.ScreenUrns.Contains(u.Name, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            Log.Error("[medius/{Extension}] urn {Urn} is one of the game's own screens; it is left out of the URN store", Name, urn.Name);
            _settings.Urns.Remove(urn);
        }
        if (context is not null)
        {
            FindAccount ??= name => context.Lobby.Accounts.Find(title.Descriptor.TitleId, name);
            NameOf ??= id => context.Lobby.Accounts.Find(id)?.Name;
            AccountDataStore data = new(context.Settings.DataDir);
            BuddyStore buddies = new(data);
            BuddiesOf ??= id => buddies.Get(title.Descriptor.TitleId, id).Buddies;
            Stats ??= new Killzone3Stats(data, title.Descriptor.TitleId, all.Stats);
        }
        if (Stats?.Settings.Slots.Count == 0)
            Log.Warning("[medius/{Extension}] no stat slots in the title's Stats settings: finishgame reports are not kept", Name);
        Log.Information("[medius/{Extension}] web service under {Root} for {Hosts}: {Urns} URNs, {Regions} regions", Name, _settings.RootPath,
            _hosts.Length == 0 ? "any host" : string.Join(", ", _hosts), _settings.Urns.Count, _settings.Regions.Count);
    }

    /// <summary>Empty by default: the service answers any host, but only below its root path.</summary>
    public IReadOnlyCollection<string> WebHosts => _hosts;

    public MediusWebResponse? HandleWeb(MediusWebRequest request)
    {
        string root = RootPath;
        if (!request.Path.StartsWith(root, StringComparison.Ordinal))
            return null;
        string rest = request.Path[root.Length..];
        Killzone3Session? session = null;
        if (rest.StartsWith(SessionSegment, StringComparison.Ordinal))
        {
            int end = rest.IndexOf('/', SessionSegment.Length);
            string token = end < 0 ? rest[SessionSegment.Length..] : rest[SessionSegment.Length..end];
            _sessions.TryGetValue(token, out session);
            rest = end < 0 ? "" : rest[(end + 1)..];
        }
        string? mac = request.Headers.GetValueOrDefault("x-svomac");
        Log.Debug("[medius/{Extension}] {Request} session {Session} mac {Mac} ({Recipe}) headers {Headers} body {Body}", Name, request,
            session?.AccountName, mac, mac is null ? "-" : Killzone3Mac.Recipe(request, root, session?.Nonce ?? "", mac) ?? "no recipe matches",
            string.Join("; ", request.Headers.Select(h => $"{h.Key}={h.Value}")), Convert.ToHexString(request.Body));

        if (rest == _settings.LoginPath)
            return Login(request);
        if (rest == _settings.RegionsPath)
            return Regions();
        if (_settings.Urns.FirstOrDefault(u => u.Path == rest) is { } urn)
            return UrnData(request, urn, session);
        Log.Warning("[medius/{Extension}] {Request}: nothing at {Path}", Name, request, rest);
        return MediusWebResponse.NotFound();
    }

    /// <summary>The service's root path, with one leading and one trailing slash.</summary>
    private string RootPath => "/" + _settings.RootPath.Trim('/') + (_settings.RootPath.Trim('/').Length > 0 ? "/" : "");

    /// <summary>
    /// kin/account/nplogin.do: the player is the NP ticket's online id when the request
    /// carries a ticket, else the <c>id</c> field (query or form body).
    /// </summary>
    private MediusWebResponse Login(MediusWebRequest request)
    {
        NpTicket? ticket = NpTicket.Find(request.Body);
        string? onlineId = ticket?.OnlineId ?? request.QueryString("id") ?? FormField(request, "id");
        MediusAccount? account = onlineId is null ? null : FindAccount?.Invoke(onlineId);
        if (account is null)
        {
            Log.Warning("[medius/{Extension}] {Request}: login for {OnlineId} ({Source}), no such account", Name, request,
                onlineId ?? "(none)", ticket is null ? "no ticket" : "ticket");
            return Xml(new XmlBuilder().Element("loginresult", _settings.FailureResult.ToString()).Document(_settings.LoginRoot));
        }

        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Killzone3Session session = new(token, account.AccountId, account.Name, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
        _sessions[token] = session;
        Log.Information("[medius/{Extension}] {Request}: login {Account} #{Id} ({Source}), session {Session}", Name, request, account.Name,
            account.AccountId, ticket is null ? "id field" : "ticket", token);
        return Xml(LoginAnswer(session, BaseUrl(request)));
    }

    /// <summary>The login answer for a new session (see the class remarks for the elements).</summary>
    public string LoginAnswer(Killzone3Session session, string baseUrl)
    {
        string sessionBase = $"{baseUrl}{SessionSegment}{session.Token}/";
        XmlBuilder xml = new XmlBuilder()
            .Element("loginresult", _settings.SuccessResult.ToString())
            .Element("nonce", session.Nonce)
            .Open("urnstore");
        foreach (Killzone3Urn urn in _settings.Urns)
            xml.Open("urn").Element("name", urn.Name).Element("uri", sessionBase + urn.Path).Close("urn");
        xml.Close("urnstore");
        if (Stats is not null)
        {
            Killzone3Career? career = Stats.Find(session.AccountId);
            if (career is not null)
                PersonalStats(xml, "personalstats", Stats.Totals(career), career.Version);
            xml.Open("friendsboard");
            Board(xml, session.AccountId);
            xml.Close("friendsboard");
        }
        if (_settings.ClientConfig.Count > 0)
        {
            // Each child is a name and its text value, handed to the game's settings
            // (0x8b6600 -> 0xb86ec4) [C].
            xml.Open("client-config");
            foreach ((string name, string value) in _settings.ClientConfig)
                xml.Element(name, value);
            xml.Close("client-config");
        }
        return xml.Document(_settings.LoginRoot);
    }

    /// <summary>
    /// kin/stats/region_list.jsp: the regions CONNECT/REGIONSELECT.GUM and
    /// COMMON/REGIONSETTINGS.GUM list, read as <c>region[n]/id</c> (named by the
    /// localized <c>menu_region_&lt;id&gt;</c>, 1-6 on the disc) and
    /// <c>region[n]/mediuslocation</c> [data]. The root element's name is a [guess].
    /// </summary>
    private MediusWebResponse Regions()
    {
        XmlBuilder xml = new();
        foreach (Killzone3Region region in _settings.Regions)
            xml.Open("region").Element("id", region.Id.ToString()).Element("mediuslocation", region.MediusLocation.ToString()).Close("region");
        return Xml(xml.Document());
    }

    /// <summary>
    /// A URN the login handed out. Its answer is the title's file for it when there is one
    /// (DataDir), else an empty document, logged so a live run shows which URNs the game
    /// asks for and with what fields.
    /// </summary>
    private MediusWebResponse UrnData(MediusWebRequest request, Killzone3Urn urn, Killzone3Session? session)
    {
        switch (urn.Answer)
        {
            case Killzone3UrnAnswer.CreateGame:
                return Xml(CreateGame(request, session));
            case Killzone3UrnAnswer.FinishGame:
                return Xml(FinishGame(request, session));
            case Killzone3UrnAnswer.PlayerStats:
                return Xml(PlayerStats(request, session));
            case Killzone3UrnAnswer.FriendsLeaderboard:
                return Xml(FriendsLeaderboard(request, session));
            case Killzone3UrnAnswer.FriendTopLeaderboard:
                return Xml(FriendTopLeaderboard(session));
            case Killzone3UrnAnswer.Result:
                Log.Information("[medius/{Extension}] {Request}: urn {Urn} for {Account}: {Result}", Name, request, urn.Name, session?.AccountName, _settings.GameResultOk);
                return Xml(new XmlBuilder().Element("result", _settings.GameResultOk).Document());
        }
        if (DataFile(urn.File) is { } file)
        {
            Log.Information("[medius/{Extension}] {Request}: urn {Urn} for {Account} from {File}", Name, request, urn.Name, session?.AccountName, file);
            return Xml(File.ReadAllText(file));
        }
        Log.Warning("[medius/{Extension}] {Request}: urn {Urn} for {Account} has no data yet; query {Query}", Name, request, urn.Name,
            session?.AccountName, string.Join("&", request.Query.Select(q => $"{q.Key}={q.Value}")));
        return Xml(new XmlBuilder().Document());
    }

    /// <summary>
    /// urn:creategame, asked by the host once its level is up (request builder 0x51d9bc:
    /// gameName, scertGameID, hostPlayerList, remotePlayerList, timePerDiv, numDivs, gameMode,
    /// clan1ID/clan2ID for clan games). The answer (0x51e3c8, state 2) is read from the
    /// root's children: <c>result</c> must be "OK" (else error 0x306), <c>svogameid</c> a
    /// non-zero number (else 0x307/0x308), which the game keeps for its finishgame report;
    /// <c>winmod</c>/<c>losemod</c>/<c>drawmod</c> are optional floats (default 1.0) [C].
    /// An empty answer ended the match about 30 s after it loaded [live 2026-11-08].
    /// </summary>
    public string CreateGame(MediusWebRequest request, Killzone3Session? session)
    {
        long id = Interlocked.Increment(ref _nextGameId);
        Killzone3Game game = new(id, request.QueryString("gameName") ?? "", request.QueryString("scertGameID") ?? "",
            request.QueryString("gameMode") ?? "", request.QueryString("hostPlayerList") ?? "", session?.AccountName ?? "", DateTime.UtcNow);
        _games[id] = game;
        Log.Information("[medius/{Extension}] {Request}: game {Game} created by {Account} as svogameid {Id} (query {Query})", Name, request,
            game.GameName, game.Host, id, string.Join("&", request.Query.Select(q => $"{q.Key}={q.Value}")));
        return new XmlBuilder().Element("result", _settings.GameResultOk).Element("svogameid", id.ToString()).Document();
    }

    /// <summary>
    /// urn:finishgame, the end-of-game report (builder 0x51d124): SVOGameID, endStatus, gameMode
    /// and <c>stats</c>, the player's values by column (see <see cref="Killzone3StatsSettings.Slots"/>),
    /// plus <c>tourneystats</c> in tournaments [C; live 2026-11-08]. The stats are added to the
    /// reporter's career. The answer (0x51e3c8, state 4) needs <c>result</c> "OK"; an optional
    /// <c>skillrating</c> (integer) becomes the player's SkillRating stat [C]. No rating is
    /// worked out here, so none is sent.
    /// </summary>
    public string FinishGame(MediusWebRequest request, Killzone3Session? session)
    {
        string? id = request.QueryString("SVOGameID") ?? request.QueryString("svogameid");
        Killzone3Game? game = long.TryParse(id, out long value) ? _games.GetValueOrDefault(value) : null;
        Log.Information("[medius/{Extension}] {Request}: game {Game} finished, reported by {Account} (endStatus {EndStatus}, gameMode {GameMode})", Name, request,
            game?.GameName ?? $"svogameid {id ?? "?"}", session?.AccountName, request.QueryString("endStatus"), request.QueryString("gameMode"));
        if (Stats is not null && session is not null && request.QueryString("stats") is { Length: > 0 } stats &&
            int.TryParse(request.QueryString("gameMode"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode))
        {
            Killzone3Career career = Stats.Add(session.AccountId, mode, stats);
            Dictionary<string, long> totals = Stats.Totals(career);
            Log.Information("[medius/{Extension}] {Account} career now {Stats}", Name, session.AccountName,
                string.Join(" ", Stats.Settings.LeaderboardStats.Select(s => $"{s}={totals.GetValueOrDefault(s)}")));
        }
        return new XmlBuilder().Element("result", _settings.GameResultOk).Document();
    }

    /// <summary>
    /// urn:playerstats:, field <c>accountID</c> (COMPARE_PLAYERS.GUM's other player, 0x8ce930). The
    /// answer handler reads <c>playerstats</c> below the root: <c>accountname</c>, then for each
    /// stat slot a child named by its column (0x8cea1c), like personalstats [C].
    /// </summary>
    public string PlayerStats(MediusWebRequest request, Killzone3Session? session)
    {
        int accountId = int.TryParse(request.QueryString("accountID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value : session?.AccountId ?? 0;
        XmlBuilder xml = new();
        Killzone3Career? career = Stats?.Find(accountId);
        PersonalStats(xml, "playerstats", Stats?.Totals(career) ?? [], career?.Version, NameOf?.Invoke(accountId) ?? "");
        Log.Information("[medius/{Extension}] {Request}: playerstats of #{Id} for {Account}", Name, request, accountId, session?.AccountName);
        return xml.Document();
    }

    /// <summary>
    /// urn:friendsleaderboard:, field <c>gamemode</c>: comma-separated gameMode numbers, one board
    /// each (the client sends types 0-3 of region 0, 0x8cb830). The answer is read as
    /// <c>leaderboards/leaderboard[n]</c> with <c>name</c> (the tab), <c>mystats</c> and
    /// <c>offline/player</c>; the client moves online friends into <c>online</c> itself and adds
    /// each player's <c>rankicon</c> from GamePoints (0x8cb464, 0x8cbafc; SOLO.GUM) [C; data].
    /// </summary>
    public string FriendsLeaderboard(MediusWebRequest request, Killzone3Session? session)
    {
        XmlBuilder xml = new XmlBuilder().Open("leaderboards");
        if (Stats is not null && session is not null)
        {
            foreach (string part in (request.QueryString("gamemode") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode) || Stats.Decode(mode) is not { } filter)
                    continue;
                List<(int Id, Dictionary<string, long> Totals)> players = Ranked(session.AccountId, filter);
                xml.Open("leaderboard").Element("name", Stats.Settings.GameTypeNames.ElementAtOrDefault(filter.Type) ?? mode.ToString());
                Dictionary<string, long> mine = players.FirstOrDefault(p => p.Id == session.AccountId).Totals ?? [];
                LeaderboardPlayer(xml, "mystats", session.AccountId, mine);
                xml.Open("offline");
                foreach ((int id, Dictionary<string, long> totals) in players)
                    LeaderboardPlayer(xml, "player", id, totals);
                xml.Close("offline").Open("online").Close("online").Close("leaderboard");
            }
        }
        xml.Close("leaderboards");
        Log.Information("[medius/{Extension}] {Request}: leaderboards for {Account}", Name, request, session?.AccountName);
        return xml.Document();
    }

    /// <summary>
    /// urn:friendtopleaderboard (STATISTICS.GUM's friendstopn, 0x8cdcec): the answer's
    /// <c>leaderboard</c> is read like the login's <c>friendsboard</c>, a list of players with
    /// <c>accountname</c>, <c>accountid</c> and their stats by name (0x8ce16c -> 0x8b4e08) [C].
    /// </summary>
    public string FriendTopLeaderboard(Killzone3Session? session)
    {
        XmlBuilder xml = new XmlBuilder().Open("leaderboard");
        if (session is not null)
            Board(xml, session.AccountId);
        return xml.Close("leaderboard").Document();
    }

    /// <summary>
    /// Stats by column, as the login's personalstats (0x51bae4) and playerstats (0x8cea1c) read
    /// them: one child per Cached slot, named by its column; personalstats takes its last child as
    /// the version, and ignores totals older than the ones it has for two hours [C].
    /// </summary>
    private void PersonalStats(XmlBuilder xml, string element, Dictionary<string, long> totals, long? version, string? accountName = null)
    {
        xml.Open(element);
        if (accountName is not null)
            xml.Element("accountname", accountName);
        foreach (Killzone3StatSlot slot in Stats?.Settings.Slots.Where(s => s.Cached) ?? [])
            xml.Element(slot.Column.ToString(CultureInfo.InvariantCulture), totals.GetValueOrDefault(slot.Key).ToString(CultureInfo.InvariantCulture));
        if (version is { } v)
            xml.Element("version", v.ToString(CultureInfo.InvariantCulture));
        xml.Close(element);
    }

    /// <summary>The top players' entries (friendsboard, friendtopleaderboard), best first.</summary>
    private void Board(XmlBuilder xml, int accountId)
    {
        foreach ((int id, Dictionary<string, long> totals) in Ranked(accountId, null).Take(Math.Max(0, Stats?.Settings.FriendTopCount ?? 0)))
            LeaderboardPlayer(xml, "player", id, totals);
    }

    private void LeaderboardPlayer(XmlBuilder xml, string element, int id, Dictionary<string, long> totals)
    {
        xml.Open(element).Element("accountid", id.ToString(CultureInfo.InvariantCulture)).Element("accountname", NameOf?.Invoke(id) ?? "");
        foreach (string stat in Stats?.Settings.LeaderboardStats ?? [])
            xml.Element(stat, totals.GetValueOrDefault(stat).ToString(CultureInfo.InvariantCulture));
        xml.Close(element);
    }

    /// <summary>The players a board lists (with <paramref name="accountId"/> always among them), best <see cref="Killzone3StatsSettings.RankStat"/> first.</summary>
    private List<(int Id, Dictionary<string, long> Totals)> Ranked(int accountId, (int Type, int Region)? filter)
    {
        if (Stats is null)
            return [];
        IEnumerable<int> ids = Stats.Settings.LeaderboardPlayers == Killzone3LeaderboardPlayers.Buddies
            ? BuddiesOf?.Invoke(accountId) ?? []
            : Stats.Players();
        return [.. ids.Append(accountId).Distinct()
            .Select(id => (id, Stats.Totals(Stats.Find(id), filter)))
            .OrderByDescending(p => p.Item2.GetValueOrDefault(Stats.Settings.RankStat))
            .ThenBy(p => p.id)];
    }

    /// <summary>The base URL the client used (scheme, host and root), so URNs point back at the same server.</summary>
    private string BaseUrl(MediusWebRequest request)
    {
        string host = request.Headers.GetValueOrDefault("host") is { Length: > 0 } h ? h : Expand(MediusUniverse.PublicHostPlaceholder);
        return $"http://{host}{RootPath}";
    }

    private string? DataFile(string name)
    {
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(_settings.DataDir) || string.IsNullOrEmpty(_title.Descriptor.ResourceDir))
            return null;
        string root = Path.GetFullPath(Path.Combine(_title.Descriptor.ResourceDir, "medius", _settings.DataDir));
        string file = Path.GetFullPath(Path.Combine(root, name));
        return file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(file) ? file : null;
    }

    private static string? FormField(MediusWebRequest request, string name) =>
        request.Body.Length > 0 && !request.Body.Contains((byte)0)
            ? MediusWebRequest.ParseQuery(Encoding.ASCII.GetString(request.Body)).GetValueOrDefault(name)
            : null;

    private string Expand(string text) =>
        _context is null ? text : text.Replace(MediusUniverse.PublicHostPlaceholder, _context.PublicAddress.ToString(), StringComparison.Ordinal);

    private static MediusWebResponse Xml(string body) => new() { ContentType = "text/xml", Body = Encoding.UTF8.GetBytes(body) };

    /// <summary>Builds the <c>data</c> documents the client reads; text is escaped.</summary>
    private sealed class XmlBuilder
    {
        private readonly StringBuilder _xml = new();

        public XmlBuilder Element(string name, string text)
        {
            _xml.Append('<').Append(name).Append('>').Append(SecurityElement.Escape(text)).Append("</").Append(name).Append('>');
            return this;
        }

        public XmlBuilder Open(string name)
        {
            _xml.Append('<').Append(name).Append('>');
            return this;
        }

        public XmlBuilder Close(string name)
        {
            _xml.Append("</").Append(name).Append('>');
            return this;
        }

        /// <summary>
        /// The elements inside <c>data</c>; inside <paramref name="root"/> too when given. A page's
        /// data source binds the root element's children (region_list.jsp's <c>region</c> under a
        /// <c>data</c> root worked [live]); the login parser wants <c>data</c> below the root.
        /// </summary>
        public string Document(string? root = null)
        {
            string data = $"<data>{_xml}</data>";
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + (string.IsNullOrEmpty(root) ? data : $"<{root}>{data}</{root}>");
        }
    }
}

/// <summary>A game the host registered through urn:creategame, by the svogameid it was given.</summary>
public sealed record Killzone3Game(long Id, string GameName, string ScertGameId, string GameMode, string HostPlayers, string Host, DateTime CreatedUtc);

/// <summary>One web login: the token in its URNs' paths, the player, and the nonce the client hashes into its MACs.</summary>
public sealed record Killzone3Session(string Token, int AccountId, string AccountName, string Nonce);

/// <summary>
/// The X-SVOMac header of Killzone 3's own HTTP client (0x132bc0): MD5 (init 0x126d8)
/// over string A (request +4), string B (the request's vtable +0x10), the body when there
/// is one, then the login nonce, as lower-case hex [C]. Which strings A and B are is not
/// known yet, so <see cref="Recipe"/> names the candidate that matches a request's MAC
/// for the log; nothing is refused on it.
/// </summary>
public static class Killzone3Mac
{
    public static string Compute(string a, string b, ReadOnlySpan<byte> body, string nonce)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(Encoding.ASCII.GetBytes(a));
        md5.AppendData(Encoding.ASCII.GetBytes(b));
        if (!body.IsEmpty)
            md5.AppendData(body);
        md5.AppendData(Encoding.ASCII.GetBytes(nonce));
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>The first candidate (A, B) pair whose MAC is <paramref name="mac"/>, named; null when none is.</summary>
    public static string? Recipe(MediusWebRequest request, string root, string nonce, string mac)
    {
        string host = request.Headers.GetValueOrDefault("host") ?? "";
        string query = request.Target.Contains('?') ? request.Target.Split('?', 2)[1] : "";
        string url = $"http://{host}{request.Target}";
        (string Name, string A, string B)[] candidates =
        [
            ("target", request.Target, ""),
            ("path+query", request.Path, query),
            ("url", url, ""),
            ("url-without-query+query", $"http://{host}{request.Path}", query),
            ("host+target", host, request.Target),
            ("path-under-root+query", request.Path.StartsWith(root, StringComparison.Ordinal) ? request.Path[root.Length..] : request.Path, query),
        ];
        string sent = mac.Trim().ToLowerInvariant();
        foreach ((string name, string a, string b) in candidates)
        {
            if (Compute(a, b, request.Body, nonce) == sent)
                return name;
            if (nonce.Length > 0 && Compute(a, b, request.Body, "") == sent)
                return name + " (no nonce)";
        }
        return null;
    }
}
