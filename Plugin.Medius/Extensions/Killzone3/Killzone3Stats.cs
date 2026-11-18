using Plugin.Medius.Lobby;
using System.Globalization;

namespace Plugin.Medius.Extensions.Killzone3;

/// <summary>
/// The stats side of the web service: what finishgame's <c>stats</c> list means and how the
/// stats and leaderboard answers are built from it.
/// </summary>
public sealed class Killzone3StatsSettings
{
    /// <summary>
    /// The columns of finishgame's <c>stats</c> list, from the game's ScoreProcessor resource
    /// (MPStats, class Stat: Column, ID, Type, Cached, Faction, Custom; RTTI 0x13e4018) [data]. The
    /// client writes the value of stat ID (for an entity stat: of entity Type) at Column (builder
    /// 0x51c5f8) and keys its own stat cache by ID + Type (<see cref="Killzone3StatSlot.Key"/>);
    /// Cached stats are the ones the cache keeps and the login's <c>personalstats</c> restores
    /// (0x51bae4) [C].
    /// </summary>
    public List<Killzone3StatSlot> Slots { get; set; } = [];

    /// <summary>Stats kept at their best value instead of summed; the client does this for HighKillSpree (0x51c5f8) [C].</summary>
    public List<string> Best { get; set; } = ["HighKillSpree"];

    /// <summary>Stats that replace the stored value instead of adding to it [guess: the ratings].</summary>
    public List<string> Latest { get; set; } = ["SkillRating", "RD"];

    /// <summary>
    /// How a gameMode number is made from a game type and a stat region (0x51db40, non-clan
    /// games): Base + Step * (TypesPerRegion * region + type) [C]. finishgame's gameMode 23 is
    /// region 2 type 3 [live]; the leaderboards ask for region 0, types 0-3.
    /// </summary>
    public int GameModeBase { get; set; } = 1;
    public int GameModeStep { get; set; } = 2;
    public int GameTypesPerRegion { get; set; } = 4;

    /// <summary>Leaderboard names by game type (EGameType: 0 Overall, 1 Warzone, 2 Operations, 3 Skirmish, table 0xeae684) [C].</summary>
    public List<string> GameTypeNames { get; set; } = ["Overall", "Warzone", "Operations", "Skirmish"];

    /// <summary>The stats each leaderboard player carries (SOLO.GUM shows GamePoints, Kills, KillsAssists, Deaths) [data].</summary>
    public List<string> LeaderboardStats { get; set; } = ["GamePoints", "Kills", "KillsAssists", "Deaths"];

    /// <summary>The stat the leaderboards rank by; the client also turns it into each player's rank icon (0x8cbafc) [C].</summary>
    public string RankStat { get; set; } = "GamePoints";

    /// <summary>Who the "friends" leaderboards list (by name in title.json).</summary>
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Killzone3LeaderboardPlayers>))]
    public Killzone3LeaderboardPlayers LeaderboardPlayers { get; set; } = Killzone3LeaderboardPlayers.All;

    /// <summary>How many players the top-friends board sends (the client shows up to 3, 0x8cdcec) [C].</summary>
    public int FriendTopCount { get; set; } = 3;
}

public enum Killzone3LeaderboardPlayers
{
    /// <summary>Everyone on this server with stats.</summary>
    All,
    /// <summary>The player and their Medius buddies.</summary>
    Buddies,
}

public sealed class Killzone3StatSlot
{
    public int Column { get; set; }
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Cached { get; set; }
    public bool Faction { get; set; }
    /// <summary>Per-match values the client fills itself (times, map, end status); never summed.</summary>
    public bool Custom { get; set; }

    /// <summary>The name the client's stat cache uses: ID then Type ("%s%s", 0x51c5f8) [C].</summary>
    public string Key => Id + Type;
}

/// <summary>A player's totals: per finishgame gameMode, stat key to value.</summary>
public sealed class Killzone3Career
{
    /// <summary>When the totals last changed (unix seconds); the login's personalstats version.</summary>
    public long Version { get; set; }
    public Dictionary<int, Dictionary<string, long>> Modes { get; set; } = [];
}

/// <summary>Killzone 3's career stats, kept per account as AccountDataStore key <see cref="Key"/>.</summary>
public sealed class Killzone3Stats(AccountDataStore data, string titleId, Killzone3StatsSettings settings)
{
    public const string Key = "career";

    private readonly object _lock = new();

    public Killzone3StatsSettings Settings => settings;

    public Killzone3Career? Find(int accountId) => data.Read<Killzone3Career>(titleId, accountId, Key);

    public IEnumerable<int> Players() => data.AccountsWith(titleId, Key);

    /// <summary>Adds one finishgame <c>stats</c> list (comma-separated, by column) played in <paramref name="gameMode"/>.</summary>
    public Killzone3Career Add(int accountId, int gameMode, string stats)
    {
        string[] values = stats.Split(',');
        lock (_lock)
        {
            Killzone3Career career = Find(accountId) ?? new Killzone3Career();
            if (!career.Modes.TryGetValue(gameMode, out Dictionary<string, long>? totals))
                career.Modes[gameMode] = totals = [];
            foreach (Killzone3StatSlot slot in settings.Slots)
            {
                if (slot.Custom || slot.Column >= values.Length ||
                    !long.TryParse(values[slot.Column], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                    continue;
                long old = totals.GetValueOrDefault(slot.Key);
                totals[slot.Key] = settings.Latest.Contains(slot.Key) ? value
                    : settings.Best.Contains(slot.Key) ? Math.Max(old, value)
                    : old + value;
            }
            career.Version = Math.Max(career.Version + 1, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            data.Write(titleId, accountId, Key, career);
            return career;
        }
    }

    /// <summary>The game type and stat region of a gameMode number; null for one the formula does not make (clan games).</summary>
    public (int Type, int Region)? Decode(int gameMode)
    {
        int n = gameMode - settings.GameModeBase;
        if (n < 0 || settings.GameModeStep <= 0 || settings.GameTypesPerRegion <= 0 || n % settings.GameModeStep != 0)
            return null;
        n /= settings.GameModeStep;
        return (n % settings.GameTypesPerRegion, n / settings.GameTypesPerRegion);
    }

    /// <summary>
    /// A player's totals over the games that fall in <paramref name="filter"/>: game type 0 (Overall)
    /// takes every type and region 0 every region [guess: the leaderboards ask for region 0]. Null
    /// filter: every game.
    /// </summary>
    public Dictionary<string, long> Totals(Killzone3Career? career, (int Type, int Region)? filter = null)
    {
        Dictionary<string, long> sum = [];
        if (career is null)
            return sum;
        foreach ((int mode, Dictionary<string, long> totals) in career.Modes)
        {
            if (filter is { } f && (Decode(mode) is not { } m || (f.Type != 0 && m.Type != f.Type) || (f.Region != 0 && m.Region != f.Region)))
                continue;
            foreach ((string key, long value) in totals)
                sum[key] = settings.Latest.Contains(key) ? value
                    : settings.Best.Contains(key) ? Math.Max(sum.GetValueOrDefault(key), value)
                    : sum.GetValueOrDefault(key) + value;
        }
        return sum;
    }
}
