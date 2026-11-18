using Plugin.Medius.Lobby;
using System.Globalization;

namespace Plugin.Medius.Extensions.ResistanceFallOfMan;

/// <summary>A player's career: the sums of their finishGame reports, per game mode.</summary>
public sealed class ResistanceCareer
{
    public Dictionary<int, Dictionary<string, long>> Modes { get; set; } = [];
}

/// <summary>
/// Fall of Man's career stats. Each player posts their own match to finishGameURI as a form
/// (builder 0x367bfc) [C; live 2026-11-06]: per-match counts, which are summed per game mode, and
/// the stats pages (playerStatsURI) show them under the names of the client's stat table
/// (0x100bf834). Kept per account as AccountDataStore key <see cref="Key"/>; the form's fields are
/// stored as they came, so a stat worked out later can be shown for matches already played.
/// </summary>
public sealed class ResistanceStats(AccountDataStore data, string titleId)
{
    public const string Key = "career";

    /// <summary>The form field the game's own mode id is in; DM 9, TDM 10, Breach 11, CTF 12, Meltdown 13, Conversion 14, Team Conversion 19, Assault 20 (0x34e938) [C].</summary>
    public const string ModeField = "gameMode";

    /// <summary>endStatus of a match played to its end (0x1c2424 maps the game's reason 0 to 1) [C; live].</summary>
    public const long EndedNormally = 1;

    /// <summary>The form fields that name the match instead of counting anything.</summary>
    private static readonly HashSet<string> Identity = new(StringComparer.OrdinalIgnoreCase) { "SVOGameID", "endStatus", ModeField, "mapID", "modeID" };

    /// <summary>Fields where the career keeps the best value, not the sum: the longest spree; the client's match counter.</summary>
    private static readonly HashSet<string> Best = new(StringComparer.OrdinalIgnoreCase) { "killSpree", "counter" };

    /// <summary>Stat names that are a form field summed.</summary>
    private static readonly (string Stat, string Field)[] Sums =
    [
        ("WINS", "wins"), ("LOSSES", "losses"), ("MONEY_GAINED", "money"), ("RATING_SCORE", "rating"), ("KILLS", "kills"), ("DEATHS", "deaths"),
        ("ASSISTS", "assists"), ("HEADSHOTS", "headshots"), ("STEALTH_KILLS", "stealthKills"), ("MELEE_KILLS", "meleeKills"), ("SHOTS_FIRED", "shotsFired"),
        ("KILL_SPREE", "killSpree"), ("TEAM_KILLS", "teamKills"), ("TEAMMATE_KILLS", "teamMateKills"), ("DEF_KILLS", "defKills"), ("NODES_CAP", "nodes"),
        ("NODES_RECAP", "recapNodes"), ("DAM_REACTORS", "reactor"), ("FLAGS_TAKEN", "flags"), ("FLAG_RECOV", "flagRecover"), ("FLAG_DROP", "flagDrop"),
        ("FLAG_KILLS", "flagKills"), ("TIME_HUMAN", "timeHuman"), ("TIME_HYBRID", "timeHybrid"), ("SP_COUNTER", "counter"),
        // The client reports the match's rating gain and nothing else that could be experience [guess].
        ("EXP_POINTS", "rating"),
    ];

    /// <summary>The weapons: their form field prefix and stat name prefix ("RIFILE" as the client spells it).</summary>
    private static readonly (string Field, string Stat)[] Weapons =
    [
        ("rifle", "RIFILE"), ("bullseye", "BULLSEYE"), ("shotgun", "SHOTGUN"), ("sniper", "SNIPER"), ("auger", "AUGER"), ("flame", "FLAMETHROWER"),
        ("rocket", "ROCKET"), ("arc", "ARC_CHARGER"), ("frag", "FRAG_GRENADE"), ("hedgehog", "HEDGEHOG"), ("airFuel", "AIR_FUEL_GRENADE"),
    ];

    private readonly object _lock = new();

    public ResistanceCareer? Find(int accountId) => data.Read<ResistanceCareer>(titleId, accountId, Key);

    /// <summary>Adds a finishGame form to the player's career.</summary>
    public void Add(int accountId, IReadOnlyDictionary<string, string> form)
    {
        lock (_lock)
        {
            ResistanceCareer career = Find(accountId) ?? new ResistanceCareer();
            int mode = (int)Number(form, ModeField);
            if (!career.Modes.TryGetValue(mode, out Dictionary<string, long>? stats))
                career.Modes[mode] = stats = [];

            foreach ((string field, string text) in form)
            {
                if (Identity.Contains(field) || !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                    continue;
                stats[field] = Best.Contains(field) ? Math.Max(stats.GetValueOrDefault(field), value) : stats.GetValueOrDefault(field) + value;
            }

            stats["games"] = stats.GetValueOrDefault("games") + 1;
            if (Number(form, "endStatus") != EndedNormally)
                stats["incompletes"] = stats.GetValueOrDefault("incompletes") + 1;
            stats["streak"] = Number(form, "wins") > 0 ? stats.GetValueOrDefault("streak") + 1 : 0;
            stats["bestStreak"] = Math.Max(stats.GetValueOrDefault("bestStreak"), stats["streak"]);
            data.Write(titleId, accountId, Key, career);
        }
    }

    /// <summary>
    /// What a stats page shows for <paramref name="gameMode"/>, by stat name. The client's ids not in
    /// <see cref="ModeField"/>'s list (it asks for 15, then 0, for the first page [live]) show
    /// the whole career [guess]. Stats nothing feeds are left out: the page's 0 stays.
    /// </summary>
    public Dictionary<string, string> View(int accountId, int gameMode)
    {
        Dictionary<string, long> totals = Totals(Find(accountId), gameMode);
        Dictionary<string, string> view = new()
        {
            ["GAMES_PLAYED"] = Text(totals, "games"),
            ["INCOMPLETES"] = Text(totals, "incompletes"),
            // [guess] the best run of wins; nothing the client sends says which streak the page means.
            ["STREAKS"] = Text(totals, "bestStreak"),
        };
        foreach ((string stat, string field) in Sums)
            view[stat] = Text(totals, field);

        long hits = 0, misses = 0;
        foreach ((string field, string stat) in Weapons)
        {
            view[$"{stat}_KILLS"] = Text(totals, field + "Kills");
            view[$"{stat}_ASST"] = Text(totals, field + "Asst");
            view[$"{stat}_DEATHS"] = Text(totals, field + "Deaths");
            long weaponHits = totals.GetValueOrDefault(field + "Hits"), weaponMisses = totals.GetValueOrDefault(field + "Miss");
            view[$"{stat}_ACC"] = Percent(weaponHits, weaponMisses);
            hits += weaponHits;
            misses += weaponMisses;
        }
        view["TEMP_ACCURACY"] = Percent(hits, misses);

        long deaths = totals.GetValueOrDefault("deaths"), kills = totals.GetValueOrDefault("kills");
        view["KD_RATIO"] = (deaths == 0 ? kills : Math.Round((double)kills / deaths, 2)).ToString(CultureInfo.InvariantCulture);
        view["AVG_LIFESPAN"] = Average(totals, "lifeSpan", "games");
        view["AVG_FLAG_TIME"] = Average(totals, "flagTime", "flags");
        return view;
    }

    private static Dictionary<string, long> Totals(ResistanceCareer? career, int gameMode)
    {
        Dictionary<string, long> totals = new(StringComparer.OrdinalIgnoreCase);
        if (career is null)
            return totals;
        bool whole = !IsGameMode(gameMode);
        foreach ((int mode, Dictionary<string, long> stats) in career.Modes)
        {
            if (!whole && mode != gameMode)
                continue;
            foreach ((string field, long value) in stats)
                totals[field] = Best.Contains(field) || field == "bestStreak"
                    ? Math.Max(totals.GetValueOrDefault(field), value) : totals.GetValueOrDefault(field) + value;
        }
        return totals;
    }

    private static bool IsGameMode(int gameMode) => gameMode is >= 9 and <= 14 or 19 or 20;

    private static long Number(IReadOnlyDictionary<string, string> form, string field) =>
        form.TryGetValue(field, out string? text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;

    private static string Text(Dictionary<string, long> totals, string field) => totals.GetValueOrDefault(field).ToString(CultureInfo.InvariantCulture);

    private static string Percent(long hits, long misses) =>
        (hits + misses == 0 ? 0 : 100 * hits / (hits + misses)).ToString(CultureInfo.InvariantCulture);

    private static string Average(Dictionary<string, long> totals, string field, string count)
    {
        long by = totals.GetValueOrDefault(count);
        return (by == 0 ? 0 : totals.GetValueOrDefault(field) / by).ToString(CultureInfo.InvariantCulture);
    }
}
