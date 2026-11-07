using Plugin.Medius.Lobby;
using Plugin.Medius.Web;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Plugin.Medius.Extensions.Resistance2;

/// <summary>
/// SCEA's Resistance 2 stats service ("igloo" and "sync" hosts, HTTPS on 10091 in
/// lobbyoptions.csv): stat sync (statRequest.py, statUpload.py), the progress bin
/// (getPlayerStatsBin.py), leaderboards and stat pages. Request and answer shapes
/// and their evidence are in Plugin.Medius docs/R2-STATS.md.
/// </summary>
public sealed class R2StatsService
{
    private const string StatsKey = "r2stats";
    private const string SizesKey = "r2recordsizes";

    private readonly R2StatsSettings _settings;
    private readonly MediusTitle _title;
    private readonly AccountStore _accounts;
    private readonly AccountDataStore _data;
    private readonly object _lock = new();

    public R2StatsService(R2StatsSettings settings, MediusTitle title, AccountStore accounts, AccountDataStore data)
    {
        _settings = settings;
        _title = title;
        _accounts = accounts;
        _data = data;
    }

    private string TitleId => _title.Descriptor.TitleId;

    public IReadOnlyCollection<string> Hosts => _settings.Hosts;

    public MediusWebResponse? Handle(MediusWebRequest request)
    {
        R2StatsEndpoints e = _settings.Endpoints;
        string path = request.Path;
        bool Is(string configured) => configured.Length > 0 && string.Equals(path, configured, StringComparison.OrdinalIgnoreCase);

        if (Is(e.StatRequest))
            return StatRequest(request);
        if (Is(e.StatUpload))
            return StatUpload(request);
        if (Is(e.PlayerStatsBin))
            return PlayerStatsBin(request);
        if (Is(e.PlayerStats))
            return PlayerStats(request);
        if (Is(e.Leaderboards))
            return Leaderboards();
        if (Is(e.LeaderboardDetail))
            return LeaderboardDetail(request);
        if (Is(e.LeaderboardPosition))
            return LeaderboardPosition(request);
        if (_settings.EmptyEndpoints.Any(Is))
            return MediusWebResponse.Text("");
        Log.Warning("[medius/resistance2] stats: nothing at {Request}", request);
        return null;
    }

    // ---- stat sync ---------------------------------------------------------------------------

    /// <summary>
    /// POST statRequest.py, body = the NP ticket [C 0x275dc8, 0x276968]:
    /// <c>?type=upload&amp;modeId&amp;npId&amp;medius&amp;name&amp;dataSize&amp;timestamp&amp;priority</c> asks leave to upload and is
    /// answered with key=value pairs (parser 0x275c28; timestamp, waitTime, response and modeId
    /// required, downloadTimestamp optional, 0x277028). The client acts only when timestamp
    /// equals its own (0x2764ac, 0x276c50), so it is echoed. response OK_TO_UPLOAD sends the
    /// upload, DUP_UPLOAD_ID clears the client's timestamp and asks again, FORCE_WAIT waits.
    /// <c>?type=download&amp;modeId&amp;npId&amp;medius&amp;name&amp;priority&amp;lastSync</c> is answered with a download blob.
    /// </summary>
    private MediusWebResponse StatRequest(MediusWebRequest request)
    {
        int modeId = (int)(request.QueryInt64("modeId") ?? -1);
        MediusAccount? account = Resolve(request.Body, request.QueryInt64("medius"), request.QueryString("name"), null);
        string type = request.QueryString("type") ?? "";

        if (type.Equals("download", StringComparison.OrdinalIgnoreCase))
        {
            if (account is null)
                return MediusWebResponse.Binary([]);
            return MediusWebResponse.Binary(Download(account, modeId));
        }

        string timestamp = request.QueryString("timestamp") ?? "0";
        string answer = "OK_TO_UPLOAD";
        uint stamp = 0;
        if (account is null || !_settings.Modes.Any(m => m.Id == modeId))
        {
            // Nobody to store it for (or a mode the title does not list): let the client wait.
            answer = "FORCE_WAIT";
            Log.Warning("[medius/resistance2] stats: upload request for mode {Mode} from {Account}: answering FORCE_WAIT",
                modeId, account?.Name ?? $"unknown player ({request.QueryString("name")})");
        }
        else
        {
            lock (_lock)
                stamp = Load(account).Modes.GetValueOrDefault(modeId)?.Stamp ?? 0;
        }
        return MediusWebResponse.Text(
            $"timestamp={timestamp}&waitTime={_settings.WaitTimeSeconds}&response={answer}&downloadTimestamp={stamp}&modeId={modeId}");
    }

    /// <summary>
    /// POST statUpload.py?modeId&amp;npId&amp;timestamp&amp;ticketSize&amp;noDownload, body = ticketSize bytes of NP
    /// ticket, then the stat upload (0x276218 mode 27 with noDownload=1, 0x2766a8 mode 6 [C]).
    /// The answer is a download of the mode's stored sections, or nothing for noDownload=1;
    /// the client skips an empty answer (0x2771d4, 0x277250).
    /// </summary>
    private MediusWebResponse StatUpload(MediusWebRequest request)
    {
        int modeId = (int)(request.QueryInt64("modeId") ?? -1);
        int ticketSize = (int)Math.Clamp(request.QueryInt64("ticketSize") ?? 0, 0, request.Body.Length);
        long? npId = request.QueryInt64("npId");
        MediusAccount? account = Resolve(request.Body.AsSpan(0, ticketSize), null, null, npId is null ? null : (uint)npId.Value);
        R2StatUpload? upload = R2StatUpload.Parse(request.Body.AsSpan(ticketSize));
        R2StatsMode? mode = _settings.Modes.FirstOrDefault(m => m.Id == modeId);

        if (account is null || upload is null || mode is null)
        {
            Log.Warning("[medius/resistance2] stats: upload for mode {Mode} not stored ({Why}); {Length} body bytes",
                modeId, account is null ? "player unknown" : upload is null ? "blob does not parse" : "mode not in title.json",
                request.Body.Length);
            return MediusWebResponse.Binary([]);
        }

        lock (_lock)
        {
            R2PlayerStats stats = Load(account);
            Dictionary<string, Dictionary<byte, int>> allSizes = _data.ReadTitle<Dictionary<string, Dictionary<byte, int>>>(TitleId, SizesKey) ?? [];
            Dictionary<byte, int> sizes = allSizes.GetValueOrDefault(modeId.ToString(CultureInfo.InvariantCulture)) ?? [];
            int knownBefore = sizes.Count;

            R2ModeStats modeStats = stats.Modes.GetValueOrDefault(modeId) ?? new R2ModeStats();
            R2StatSections.Merge(modeStats, upload, mode.UniformRecords, sizes);
            stats.Modes[modeId] = modeStats;
            stats.Id = upload.Id;
            _data.Write(TitleId, account.AccountId, StatsKey, stats);
            if (sizes.Count != knownBefore)
            {
                allSizes[modeId.ToString(CultureInfo.InvariantCulture)] = sizes;
                _data.WriteTitle(TitleId, SizesKey, allSizes);
            }
            Log.Information("[medius/resistance2] stats: {Account} uploaded mode {Mode} sections {Types} ({Bytes} record bytes); stamp {Stamp}",
                account.Name, modeId, string.Join(",", upload.Types.Select(t => $"0x{t:X2}")), upload.Records.Length, modeStats.Stamp);
        }

        return request.QueryInt64("noDownload") == 1 ? MediusWebResponse.Binary([]) : MediusWebResponse.Binary(Download(account, modeId));
    }

    private byte[] Download(MediusAccount account, int modeId)
    {
        lock (_lock)
        {
            R2PlayerStats stats = Load(account);
            if (stats.Modes.GetValueOrDefault(modeId) is not { Segments.Count: > 0 } mode)
                return [];
            (byte[] types, byte[] records) = R2StatSections.Flatten(mode);
            return R2StatDownload.Build(mode.Stamp, stats.Id, _settings.WaitTimeSeconds, types, records);
        }
    }

    /// <summary>
    /// GET getPlayerStatsBin.py?acctId=%llu&amp;lang=%s&amp;statType=prog&amp;reqWaitTime=1&amp;firstReq=1 (login,
    /// 0x2554f4) or ?acctId=%llu&amp;lang=%s&amp;type=prof (profile, 0x2a804c) on the sync host [C].
    /// acctId is the NP account id.
    /// </summary>
    private MediusWebResponse PlayerStatsBin(MediusWebRequest request)
    {
        MediusAccount? account = request.QueryUInt64("acctId") is { } npAccountId ? _accounts.FindByNpAccountId(TitleId, npAccountId) : null;
        uint id = 0;
        if (account is not null)
            lock (_lock)
                id = Load(account).Id;
        return MediusWebResponse.Binary(R2StatDownload.BuildProgressHeader(id, _settings.WaitTimeSeconds));
    }

    // ---- pages -------------------------------------------------------------------------------

    /// <summary>
    /// GET getPlayerStats.py?acctId=%s&amp;lang=%s&amp;type=&lt;page&gt; (pages totals, comp_*, coop_*), handed
    /// to Profile.swf's ParseStatWebData: line 0 a header, then one tab-separated row per stat,
    /// read as [value, label] like ParseStatList's rows [C patch_01 Profile.swf].
    /// </summary>
    private MediusWebResponse PlayerStats(MediusWebRequest request)
    {
        string page = request.QueryString("type") ?? "";
        MediusAccount? account = ulong.TryParse(request.QueryString("acctId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong npId)
            ? _accounts.FindByNpAccountId(TitleId, npId) : null;
        StringBuilder text = new(page);
        foreach (R2StatsPageRow row in _settings.PlayerStatPages.GetValueOrDefault(page) ?? [])
            text.Append('\n').Append(account is null ? 0 : Value(account, row.Source)).Append('\t').Append(row.Label);
        return MediusWebResponse.Text(text.ToString());
    }

    /// <summary>
    /// GET getLeaderboards.py?lang=%s, handed to Leaderboard.swf's ParseLeaderboardList: one line
    /// per board, <c>id \t name \t key</c>; the part of key before '_' files it under camp, coop,
    /// spec, sold, med, comp, skr, ctf, tdm, dm or melt [C patch_03 Leaderboard.swf 0x9992].
    /// </summary>
    private MediusWebResponse Leaderboards() =>
        MediusWebResponse.Text(string.Join("\n", _settings.Leaderboards.Select(b => $"{b.Id}\t{b.Name}\t{b.Key}")));

    /// <summary>
    /// GET getLeaderboardDetail.py?ID=%d&amp;lang=%s&amp;offset=%d&amp;pagesize=10 (0x295d44), handed to
    /// ParseLeaderboardData [C 0x9eda]: line 0 the number of ranked players; line 1 a header whose
    /// field 0 is the board id and field 4 the value column's title (fields 1-3 not read; name,
    /// key and count here [guess]); then <c>rank \t hidden \t name \t value</c> rows, field 1 being
    /// spliced out before display. offset is taken as 0-based [guess].
    /// </summary>
    private MediusWebResponse LeaderboardDetail(MediusWebRequest request)
    {
        if (Board(request) is not { } board)
            return MediusWebResponse.NotFound();
        List<(MediusAccount Account, long Value)> ranking = Rank(board);
        int offset = (int)Math.Max(0, request.QueryInt64("offset") ?? 0);
        int pageSize = (int)Math.Clamp(request.QueryInt64("pagesize") ?? 10, 1, 100);
        StringBuilder text = new();
        text.Append(ranking.Count).Append('\n');
        text.Append($"{board.Id}\t{board.Name}\t{board.Key}\t{ranking.Count}\t{board.Column}");
        for (int i = offset; i < Math.Min(ranking.Count, offset + pageSize); i++)
            text.Append('\n').Append($"{i + 1}\t{ranking[i].Account.NpAccountId}\t{ranking[i].Account.Name}\t{ranking[i].Value}");
        return MediusWebResponse.Text(text.ToString());
    }

    /// <summary>
    /// GET getLeaderboardPosition.py?ID=%d&amp;lang=%s&amp;acctId=%llu (0x295d44), handed to
    /// ParseLeaderboardPosition with the player's name: <c>rank \t value</c> [C 0xa0e3]. A
    /// player not on the board gets 404, which the client shows as unranked.
    /// </summary>
    private MediusWebResponse LeaderboardPosition(MediusWebRequest request)
    {
        if (Board(request) is not { } board || request.QueryUInt64("acctId") is not { } npId)
            return MediusWebResponse.NotFound();
        List<(MediusAccount Account, long Value)> ranking = Rank(board);
        int index = ranking.FindIndex(r => r.Account.NpAccountId == npId);
        return index < 0 ? MediusWebResponse.NotFound() : MediusWebResponse.Text($"{index + 1}\t{ranking[index].Value}");
    }

    private R2Leaderboard? Board(MediusWebRequest request) =>
        request.QueryInt64("ID") is { } id ? _settings.Leaderboards.FirstOrDefault(b => b.Id == id) : null;

    /// <summary>Players with a non-zero value, best first (ties by name).</summary>
    private List<(MediusAccount Account, long Value)> Rank(R2Leaderboard board) =>
        [.. _data.AccountsWith(TitleId, StatsKey)
            .Select(id => _accounts.Find(id))
            .Where(a => a is not null && a.TitleId == TitleId)
            .Select(a => (Account: a!, Value: Value(a!, board.Source)))
            .Where(r => r.Value != 0)
            .OrderByDescending(r => r.Value).ThenBy(r => r.Account.Name, StringComparer.OrdinalIgnoreCase)];

    /// <summary>A stat summed over the sections a source names, from the player's stored records.</summary>
    public long Value(MediusAccount account, R2StatSource source)
    {
        if (!_settings.Fields.TryGetValue(source.Field, out R2StatField? field))
            return 0;
        lock (_lock)
        {
            R2ModeStats? mode = Load(account).Modes.GetValueOrDefault(source.Mode);
            if (mode is null)
                return 0;
            long total = 0;
            foreach (byte type in source.SectionTypes)
                if (R2StatSections.RecordOf(mode, type) is { } record && field.Offset + field.Size <= record.Length)
                    total += field.Size switch
                    {
                        1 => record[field.Offset],
                        2 => BinaryPrimitives.ReadUInt16BigEndian(record.AsSpan(field.Offset)),
                        8 => (long)BinaryPrimitives.ReadUInt64BigEndian(record.AsSpan(field.Offset)),
                        _ => BinaryPrimitives.ReadUInt32BigEndian(record.AsSpan(field.Offset)),
                    };
            return total;
        }
    }

    // ---- players -----------------------------------------------------------------------------

    /// <summary>
    /// The account a request is about: by the NP ticket in its body (whose account id is then
    /// remembered on the account), else by Medius account id, online id, or the low 32 bits of
    /// the NP account id the client printed with %d/%u.
    /// </summary>
    private MediusAccount? Resolve(ReadOnlySpan<byte> ticketBytes, long? mediusAccountId, string? onlineId, uint? npIdLow32)
    {
        NpTicket? ticket = NpTicket.Parse(ticketBytes);
        MediusAccount? account = null;
        if (ticket is not null)
            account = _accounts.FindByNpAccountId(TitleId, ticket.AccountId) ?? _accounts.Find(TitleId, ticket.OnlineId);
        if (account is null && mediusAccountId is { } id && _accounts.Find((int)id) is { } byId && byId.TitleId == TitleId)
            account = byId;
        if (account is null && !string.IsNullOrEmpty(onlineId))
            account = _accounts.Find(TitleId, onlineId);
        if (account is null && npIdLow32 is { } low)
            account = _accounts.FindByNpAccountIdLow32(TitleId, low);
        if (account is not null && ticket is not null && account.NpAccountId != ticket.AccountId &&
            string.Equals(account.Name, ticket.OnlineId, StringComparison.OrdinalIgnoreCase))
            _accounts.Update(account, a => a.NpAccountId = ticket.AccountId);
        return account;
    }

    private R2PlayerStats Load(MediusAccount account) => _data.Read<R2PlayerStats>(TitleId, account.AccountId, StatsKey) ?? new R2PlayerStats();
}

/// <summary>The stats service part of the resistance2 extension settings (title.json ExtensionSettings.Stats).</summary>
public sealed class R2StatsSettings
{
    /// <summary>Host names the client sends (lobbyoptions.csv LOBBY_STATS_HOSTNAME, LOBBY_STAT_SYNC_HOSTNAME).</summary>
    public List<string> Hosts { get; set; } = [];
    public R2StatsEndpoints Endpoints { get; set; } = new();
    /// <summary>Paths answered 200 with an empty body (services not worked out yet).</summary>
    public List<string> EmptyEndpoints { get; set; } = [];
    /// <summary>waitTime and the downloads' wait field: seconds before the client syncs again.</summary>
    public int WaitTimeSeconds { get; set; } = 30;
    /// <summary>The stat sync modes stored; others are answered FORCE_WAIT and not kept.</summary>
    public List<R2StatsMode> Modes { get; set; } = [];
    /// <summary>Named fields of a stat record (offset and size in bytes, big-endian).</summary>
    public Dictionary<string, R2StatField> Fields { get; set; } = [];
    public List<R2Leaderboard> Leaderboards { get; set; } = [];
    /// <summary>getPlayerStats.py pages by type (totals, comp_ctf, coop_medic...).</summary>
    public Dictionary<string, List<R2StatsPageRow>> PlayerStatPages { get; set; } = [];
}

/// <summary>The service paths (lobbyoptions.csv LOBBY_STATS_*_URI).</summary>
public sealed class R2StatsEndpoints
{
    public string StatRequest { get; set; } = "";
    public string StatUpload { get; set; } = "";
    public string PlayerStatsBin { get; set; } = "";
    public string PlayerStats { get; set; } = "";
    public string Leaderboards { get; set; } = "";
    public string LeaderboardDetail { get; set; } = "";
    public string LeaderboardPosition { get; set; } = "";
}

public sealed class R2StatsMode
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>All the mode's sections share one record layout (mode 6: mask 1 for every section).</summary>
    public bool UniformRecords { get; set; }
}

public sealed class R2StatField
{
    public int Offset { get; set; }
    public int Size { get; set; } = 4;
}

/// <summary>Where a value comes from: a field of the records of some sections of one mode, summed.</summary>
public class R2StatSource
{
    public int Mode { get; set; }
    public List<byte> SectionTypes { get; set; } = [];
    public string Field { get; set; } = "";
}

public sealed class R2Leaderboard : R2StatSource
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Category key; the part before '_' picks the menu (camp, coop, spec, sold, med, comp, skr, ctf, tdm, dm, melt).</summary>
    public string Key { get; set; } = "";
    /// <summary>Title of the value column.</summary>
    public string Column { get; set; } = "";

    public R2StatSource Source => this;
}

public sealed class R2StatsPageRow
{
    public string Label { get; set; } = "";
    public R2StatSource Source { get; set; } = new();
}
