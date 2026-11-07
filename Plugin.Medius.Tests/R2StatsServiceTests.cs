using Plugin.Medius.Extensions.Resistance2;
using Plugin.Medius.Lobby;
using Plugin.Medius.Web;
using ServerShared.Titles;
using System.Buffers.Binary;
using System.Text;

namespace Plugin.Medius.Tests;

/// <summary>
/// Resistance 2's stats service with the request shapes the 1.60 client sends, and
/// its answers read back by the client's own rules (ported below from the EBOOT):
/// the key=value parser 0x275c28 and the download/progress-bin checks of 0x2ec76c,
/// 0x2f1ce8 and 0x2f1470. See docs/R2-STATS.md.
/// </summary>
public sealed class R2StatsServiceTests : IDisposable
{
    private const string Prefix = "/resistance2/";
    private const ulong NpAccountA = 0x0123456789ABCDEF;
    private const ulong NpAccountB = 0x00000000CAFEF00D;
    private const byte Soldier = 0x17, Medic = 0x01, SpecOps = 0x23;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "r2stats-" + Guid.NewGuid().ToString("N"));
    private readonly MediusTitle _title = new() { Descriptor = new TitleDescriptor { TitleId = "resistance2", Name = "Resistance 2" } };
    private readonly AccountStore _accounts;
    private R2StatsService _service;

    public R2StatsServiceTests()
    {
        _accounts = new AccountStore(_dir);
        _service = NewService();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private R2StatsService NewService() => new(Settings(), _title, _accounts, new AccountDataStore(_dir));

    private static R2StatsSettings Settings()
    {
        R2StatSource Coop(params byte[] types) => new() { Mode = 27, SectionTypes = [.. types], Field = "Experience" };
        return new R2StatsSettings
        {
            Hosts = ["resistance2-prod.igloo.online.scea.com", "resistance2-prod.sync.online.scea.com"],
            Endpoints = new()
            {
                StatRequest = Prefix + "statRequest.py",
                StatUpload = Prefix + "statUpload.py",
                PlayerStatsBin = Prefix + "getPlayerStatsBin.py",
                PlayerStats = Prefix + "getPlayerStats.py",
                Leaderboards = Prefix + "getLeaderboards.py",
                LeaderboardDetail = Prefix + "getLeaderboardDetail.py",
                LeaderboardPosition = Prefix + "getLeaderboardPosition.py",
            },
            EmptyEndpoints = [Prefix + "getCommendations.py"],
            WaitTimeSeconds = 30,
            Modes = [new() { Id = 6, UniformRecords = true }, new() { Id = 27 }],
            Fields = new() { ["Experience"] = new() { Offset = 0, Size = 4 } },
            Leaderboards =
            [
                new() { Id = 1, Name = "Co-op Experience", Key = "coop_xp", Column = "XP", Mode = 27, SectionTypes = [Soldier, Medic, SpecOps], Field = "Experience" },
                new() { Id = 3, Name = "Medic Experience", Key = "med_xp", Column = "XP", Mode = 27, SectionTypes = [Medic], Field = "Experience" },
            ],
            PlayerStatPages = new() { ["coop_totals"] = [new() { Label = "Experience", Source = Coop(Soldier, Medic, SpecOps) }] },
        };
    }

    // ---- what the client sends --------------------------------------------------------------

    private MediusWebResponse Send(string method, string target, byte[]? body = null) =>
        _service.Handle(new MediusWebRequest
        {
            Method = method,
            Target = target,
            Headers = new Dictionary<string, string> { ["host"] = "resistance2-prod.sync.online.scea.com:10091" },
            Body = body ?? [],
        }) ?? MediusWebResponse.NotFound();

    /// <summary>A record of the given size whose first u32 (STAT_EXP_POINTS) is <paramref name="xp"/>.</summary>
    private static byte[] Record(int size, uint xp, byte fill)
    {
        byte[] record = Enumerable.Repeat(fill, size).ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(record, xp);
        return record;
    }

    private static byte[] Upload(uint id, params (byte Type, byte[] Record)[] sections) =>
        new R2StatUpload(0, id, [.. sections.Select(s => s.Type)], [.. sections.SelectMany(s => s.Record)]).Serialize();

    /// <summary>statRequest.py?type=upload as built at 0x275dc8 (npId and medius printed with %u/%d).</summary>
    private string RequestUpload(ulong npAccount, MediusAccount account, int mode, uint timestamp) =>
        Encoding.ASCII.GetString(Send("POST",
            $"{Prefix}statRequest.py?type=upload&modeId={mode}&npId={(uint)npAccount}&medius={account.AccountId}&name={account.Name}&dataSize=100&timestamp={timestamp}&priority=50",
            NpTicket.Build(npAccount, account.Name)).Body);

    /// <summary>statUpload.py as built at 0x276218 / 0x2766a8: ticket then blob, npId printed with %d.</summary>
    private MediusWebResponse StatUpload(ulong npAccount, string name, int mode, byte[] blob, int noDownload)
    {
        byte[] ticket = NpTicket.Build(npAccount, name);
        return Send("POST", $"{Prefix}statUpload.py?modeId={mode}&npId={(int)(uint)npAccount}&timestamp=0&ticketSize={ticket.Length}&noDownload={noDownload}",
            [.. ticket, .. blob]);
    }

    // ---- how the client reads answers -------------------------------------------------------

    /// <summary>0x275c28: the value of <paramref name="key"/> in a key=value answer, as the client finds it.</summary>
    private static string? ClientValue(string text, string key)
    {
        int p = 0;
        while (p < text.Length && text[p] != '\0')
        {
            int keyLength = IndexOfAny(text, p, "\n\t =") - p;
            if (keyLength == key.Length && string.CompareOrdinal(text, p, key, 0, key.Length) == 0)
            {
                int v = p + keyLength;
                while (v < text.Length && "\t =".Contains(text[v]))
                    v++;
                return text[v..IndexOfAny(text, v, "\n\t &")];
            }
            p = IndexOfAny(text, p, "\n&");
            while (p < text.Length && "\n\t &".Contains(text[p]))
                p++;
        }
        return null;
    }

    private static int IndexOfAny(string text, int from, string chars)
    {
        int found = text.IndexOfAny(chars.ToCharArray(), from);
        return found < 0 ? text.Length : found;
    }

    /// <summary>A download as 0x2ec76c / 0x2f1ce8 accept it: length and id checked, size adds up per record size.</summary>
    private static (uint Stamp, int Wait, byte[] Types, byte[] Records) ClientDownload(byte[] blob, uint expectedId, Func<byte, int> recordSize)
    {
        Assert.True(blob.Length > 0x10);
        Assert.Equal((uint)blob.Length, BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(8)));
        Assert.Equal(expectedId, BinaryPrimitives.ReadUInt32BigEndian(blob.AsSpan(4)));
        int count = blob[0x10];
        byte[] types = blob[0x11..(0x11 + count)];
        Assert.Equal(blob.Length, 0x11 + types.Sum(t => recordSize(t) + 1));
        return (BinaryPrimitives.ReadUInt32BigEndian(blob), BinaryPrimitives.ReadInt32BigEndian(blob.AsSpan(12)), types, blob[(0x11 + count)..]);
    }

    // ---- tests --------------------------------------------------------------------------------

    [Fact]
    public void Upload_request_is_answered_ok_with_the_clients_timestamp()
    {
        MediusAccount waldo = _accounts.Create("resistance2", "Waldo_717", null)!;
        string answer = RequestUpload(NpAccountA, waldo, 27, 0);

        Assert.Equal("0", ClientValue(answer, "timestamp"));
        Assert.Equal("30", ClientValue(answer, "waitTime"));
        Assert.Equal("OK_TO_UPLOAD", ClientValue(answer, "response"));
        Assert.Equal("27", ClientValue(answer, "modeId"));
        Assert.Equal("0", ClientValue(answer, "downloadTimestamp"));
        // The ticket taught the server the player's NP account id.
        Assert.Equal(NpAccountA, _accounts.Find("resistance2", "Waldo_717")!.NpAccountId);
    }

    [Fact]
    public void Unknown_player_or_mode_is_told_to_wait()
    {
        MediusAccount ghost = new() { AccountId = 99, Name = "Nobody", TitleId = "resistance2" };
        Assert.Equal("FORCE_WAIT", ClientValue(RequestUpload(NpAccountA, ghost, 27, 5), "response"));
        Assert.Equal("5", ClientValue(RequestUpload(NpAccountA, ghost, 27, 5), "timestamp"));
        MediusAccount waldo = _accounts.Create("resistance2", "Waldo_717", null)!;
        Assert.Equal("FORCE_WAIT", ClientValue(RequestUpload(NpAccountA, waldo, 13, 0), "response"));
    }

    [Fact]
    public void Coop_uploads_are_merged_and_downloaded_back_by_section()
    {
        MediusAccount waldo = _accounts.Create("resistance2", "Waldo_717", null)!;
        RequestUpload(NpAccountA, waldo, 27, 0);
        Dictionary<byte, int> sizes = new() { [Soldier] = 8, [Medic] = 12, [SpecOps] = 16 };

        // All three classes at once: sizes unknown yet, kept as one run (noDownload=1: empty answer).
        Assert.Empty(StatUpload(NpAccountA, "Waldo_717", 27,
            Upload(7, (Soldier, Record(8, 100, 1)), (Medic, Record(12, 200, 2)), (SpecOps, Record(16, 300, 3))), 1).Body);
        // Only the run's first record can be read until it splits.
        Assert.Equal(100, _service.Value(waldo, Settings().Leaderboards[0]));

        // Medic alone: its size is learnt; soldier's by subtraction is still out of reach (two unknown).
        StatUpload(NpAccountA, "Waldo_717", 27, Upload(7, (Medic, Record(12, 250, 4))), 1);
        // Soldier alone: now the old run splits (spec ops by subtraction) and both replace their sections.
        byte[] download = StatUpload(NpAccountA, "Waldo_717", 27, Upload(7, (Soldier, Record(8, 150, 5))), 0).Body;

        (uint stamp, int wait, byte[] types, byte[] records) = ClientDownload(download, 7, t => sizes[t]);
        Assert.Equal(3u, stamp);
        Assert.Equal(30, wait);
        Assert.Equal([SpecOps, Medic, Soldier], types);
        Assert.Equal([.. Record(16, 300, 3), .. Record(12, 250, 4), .. Record(8, 150, 5)], records);
        Assert.Equal(700, _service.Value(waldo, Settings().Leaderboards[0]));
        Assert.Equal(250, _service.Value(waldo, Settings().Leaderboards[1]));

        // statRequest type=download (0x276968) gets the same, and the next upload request sees the stamp.
        byte[] again = Send("POST", $"{Prefix}statRequest.py?type=download&modeId=27&npId={unchecked((uint)NpAccountA)}&medius={waldo.AccountId}&name=Waldo_717&priority=50&lastSync=0",
            NpTicket.Build(NpAccountA, "Waldo_717")).Body;
        Assert.Equal(download, again);
        Assert.Equal("3", ClientValue(RequestUpload(NpAccountA, waldo, 27, 0), "downloadTimestamp"));
    }

    [Fact]
    public void Campaign_records_split_by_their_one_layout()
    {
        _accounts.Create("resistance2", "Waldo_717", null);
        StatUpload(NpAccountA, "Waldo_717", 6, Upload(0, (7, Record(20, 1, 1)), (8, Record(20, 2, 2))), 0);
        byte[] download = StatUpload(NpAccountA, "Waldo_717", 6, Upload(0, (8, Record(20, 3, 3)), (0x11, Record(20, 4, 4))), 0).Body;

        (_, _, byte[] types, byte[] records) = ClientDownload(download, 0, _ => 20);
        Assert.Equal([7, 8, 0x11], types);
        Assert.Equal([.. Record(20, 1, 1), .. Record(20, 3, 3), .. Record(20, 4, 4)], records);
    }

    [Fact]
    public void Stats_survive_a_restart()
    {
        _accounts.Create("resistance2", "Waldo_717", null);
        StatUpload(NpAccountA, "Waldo_717", 27, Upload(9, (Medic, Record(12, 42, 0))), 1);

        _service = NewService();
        MediusAccount waldo = new AccountStore(_dir).Find("resistance2", "Waldo_717")!;
        Assert.Equal(42, _service.Value(waldo, Settings().Leaderboards[1]));
        byte[] bin = Send("GET", $"{Prefix}getPlayerStatsBin.py?acctId={NpAccountA}&lang=en&statType=prog&reqWaitTime=1&firstReq=1").Body;
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32BigEndian(bin));
    }

    [Fact]
    public void Progress_bin_is_a_header_the_client_accepts()
    {
        // 0x2f1470: [0] == player+0x601b4, [4] == length; 16 bytes = no data; [12] seconds to the next sync.
        byte[] bin = Send("GET", $"{Prefix}getPlayerStatsBin.py?acctId={NpAccountA}&lang=en&type=prof").Body;
        Assert.Equal(16, bin.Length);
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(bin));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32BigEndian(bin.AsSpan(4)));
        Assert.Equal(30, BinaryPrimitives.ReadInt32BigEndian(bin.AsSpan(12)));
    }

    [Fact]
    public void Leaderboards_read_as_Leaderboard_swf_parses_them()
    {
        _accounts.Create("resistance2", "Waldo_717", null);
        _accounts.Create("resistance2", "Dyllan500", null);
        _accounts.Create("resistance2", "Idle", null);
        StatUpload(NpAccountA, "Waldo_717", 27, Upload(0, (Medic, Record(12, 250, 0))), 1);
        StatUpload(NpAccountB, "Dyllan500", 27, Upload(0, (Medic, Record(12, 900, 0))), 1);

        // ParseLeaderboardList: id \t name \t key, category = key before '_'.
        string[] list = Encoding.ASCII.GetString(Send("GET", $"{Prefix}getLeaderboards.py?lang=en").Body).Split('\n');
        Assert.Equal(["1", "Co-op Experience", "coop_xp"], list[0].Split('\t'));
        Assert.Equal("med", list[1].Split('\t')[2].Split('_')[0]);

        // ParseLeaderboardData: total, header (id at 0, column at 4), rows with field 1 spliced out.
        string[] detail = Encoding.ASCII.GetString(Send("GET", $"{Prefix}getLeaderboardDetail.py?ID=3&lang=en&offset=0&pagesize=10").Body).Split('\n');
        Assert.Equal("2", detail[0]);
        Assert.Equal("3", detail[1].Split('\t')[0]);
        Assert.Equal("XP", detail[1].Split('\t')[4]);
        List<string> first = [.. detail[2].Split('\t')];
        first.RemoveAt(1);
        Assert.Equal(["1", "Dyllan500", "900"], first);
        Assert.Equal("Waldo_717", detail[3].Split('\t')[2]);
        Assert.Equal(4, detail.Length);

        // ParseLeaderboardPosition: rank \t value; unranked is 404.
        Assert.Equal("2\t250", Encoding.ASCII.GetString(Send("GET", $"{Prefix}getLeaderboardPosition.py?ID=3&lang=en&acctId={NpAccountA}").Body));
        Assert.Equal(404, Send("GET", $"{Prefix}getLeaderboardPosition.py?ID=3&lang=en&acctId=5").Status);
        Assert.Equal(404, Send("GET", $"{Prefix}getLeaderboardDetail.py?ID=77&lang=en&offset=0&pagesize=10").Status);
    }

    [Fact]
    public void Stat_pages_and_unworked_endpoints_answer()
    {
        _accounts.Create("resistance2", "Waldo_717", null);
        StatUpload(NpAccountA, "Waldo_717", 27, Upload(0, (Soldier, Record(8, 5, 0))), 1);
        string page = Encoding.ASCII.GetString(Send("GET", $"{Prefix}getPlayerStats.py?acctId={NpAccountA}&lang=en&type=coop_totals").Body);
        Assert.Equal("coop_totals\n5\tExperience", page);
        Assert.Equal(200, Send("GET", $"{Prefix}getCommendations.py?acctId=1").Status);
        Assert.Equal(404, Send("GET", $"{Prefix}nothing.py").Status);
    }

    [Fact]
    public void Np_ticket_round_trips()
    {
        NpTicket? ticket = NpTicket.Parse(NpTicket.Build(NpAccountA, "Waldo_717"));
        Assert.Equal(new NpTicket(NpAccountA, "Waldo_717"), ticket);
        Assert.Null(NpTicket.Parse([1, 2, 3]));
    }

    [Fact]
    public void Title_json_configures_the_stats_service()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "ServerApp-Titles", "Resistance2")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            return;
        List<string> problems = [];
        MediusTitle title = Assert.Single(MediusTitle.LoadAll(
            [new TitleDescriptor { TitleId = "resistance2", Name = "Resistance 2", ProtocolFamily = "medius", ResourceDir = Path.Combine(dir, "ServerApp-Titles", "Resistance2") }],
            problems));
        Resistance2Settings settings = System.Text.Json.JsonSerializer.Deserialize<Resistance2Settings>(title.ExtensionSettings, MediusTitle.JsonOptions)!;
        Assert.Contains("resistance2-prod.sync.online.scea.com", settings.Stats.Hosts);
        Assert.Equal("/resistance2/statUpload.py", settings.Stats.Endpoints.StatUpload);
        Assert.True(settings.Stats.Modes.Single(m => m.Id == 6).UniformRecords);
        Assert.Equal([0x17, 0x01, 0x23], settings.Stats.Leaderboards.Single(b => b.Id == 1).SectionTypes);
        Assert.Equal(0x01, Assert.Single(settings.Stats.PlayerStatPages["coop_medic"]).Source.SectionTypes.Single());
    }
}
