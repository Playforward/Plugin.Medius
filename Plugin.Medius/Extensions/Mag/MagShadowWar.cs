using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// One build's Shadow War contract bonuses, embedded as
/// <c>Data/&lt;version folder&gt;/mag-shadow-war.json</c> by
/// <c>tools/re/shadowwar.py</c> (in Plugin.MagGameServer) from
/// scripts/shadowwar_contract_bonuses.zpr: per game type with contracts, one
/// entry per contract (the PMC it first belonged to: Valor, Raven, SVER) with
/// the global modifiers its holder gets (kGlobalModXP 0.05 in Sabotage).
/// </summary>
public sealed class MagContractBonuses
{
    public const string FileName = "mag-shadow-war.json";

    /// <summary>The global modifier that adds experience: the ShadowWarBonus stat's rate.</summary>
    public const string ExperienceModifier = "kGlobalModXP";

    [JsonPropertyName("gameTypes")]
    public Dictionary<string, List<Dictionary<string, float>>> GameTypes { get; init; } = [];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, MagContractBonuses> EmbeddedTables = new();

    public static MagContractBonuses Embedded(MagClientVersion version) => EmbeddedTables.GetOrAdd(version.Protocol, _ =>
    {
        string name = version.MediusResource(FileName);
        using Stream stream = typeof(MagContractBonuses).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded");
        return JsonSerializer.Deserialize<MagContractBonuses>(stream) ?? new();
    });
}

/// <summary>How a won game moves the Shadow War. The wiki says a win adds "a little" to the PMC's bar; how much is not known.</summary>
public sealed class MagShadowWarSettings
{
    /// <summary>Points (of the bar's 100) a major victory gives the winner's PMC in that game type [guess].</summary>
    public int PointsPerMajorVictory { get; set; } = 10;
    /// <summary>Points a minor victory gives [guess].</summary>
    public int PointsPerMinorVictory { get; set; } = 5;
    /// <summary>The bar's top: the deploy screen's score bars have 101 frames, 0..100 (ui_deploy progressBlack/Green/Red) [C].</summary>
    public int BarTop { get; set; } = 100;
    /// <summary>The most contracts of one game type a PMC may hold ("never all 3", wiki Contracts).</summary>
    public int MaxContracts { get; set; } = 2;
}

/// <summary>
/// The Shadow War: for each game type with contracts, each PMC's score bar
/// and who holds each contract (contract c first belonged to PMC c; Valor,
/// Raven, SVER = 0, 1, 2). The lobby sends both in its server status
/// (<see cref="MagServerStatus.WarScores"/>, <see cref="MagServerStatus.Contracts"/>),
/// which the deploy screen shows (GametypeFactionScore, GametypeFactionContractOwner).
/// </summary>
/// <remarks>
/// The rules, from the wiki (Contracts):
/// <list type="bullet">
/// <item>Each win adds to the winning PMC's bar for that game type.</item>
/// <item>When a bar reaches the top, the PMC in first place takes the
/// contract of the PMC in last place (its own if it holds it, else one it
/// holds), second place keeps its own, and every bar of that game type starts
/// again. A last place holding none: nothing changes hands.</item>
/// <item>No PMC holds all of a game type's contracts: one already at the
/// limit first gives a contract back to second place.</item>
/// <item>The holder gets each contract's bonuses (<see cref="MagContractBonuses"/>).</item>
/// </list>
/// </remarks>
public sealed class MagShadowWar
{
    public const int Factions = 3;

    private readonly MagContractBonuses _bonuses;
    private readonly MagShadowWarSettings _settings;
    private readonly string? _path;
    private readonly object _lock = new();
    private readonly Dictionary<string, Front> _fronts;

    /// <summary>One game type's war: the PMCs' scores and each contract's holder.</summary>
    public sealed class Front
    {
        public int[] Scores { get; set; } = new int[Factions];
        public int[] Holders { get; set; } = [0, 1, 2];
    }

    public MagShadowWar(MagContractBonuses bonuses, MagShadowWarSettings settings, string? path = null)
    {
        _bonuses = bonuses;
        _settings = settings;
        _path = path;
        _fronts = Load(path) ?? [];
        foreach ((string gameType, List<Dictionary<string, float>> contracts) in bonuses.GameTypes)
            if (!_fronts.ContainsKey(gameType))
                _fronts[gameType] = new Front { Holders = [.. Enumerable.Range(0, contracts.Count)] };
    }

    /// <summary>The game types that have contracts.</summary>
    public IEnumerable<string> GameTypes => _bonuses.GameTypes.Keys;

    /// <summary>A PMC's score in a game type (0 for one without contracts).</summary>
    public int ScoreOf(string gameType, int faction)
    {
        lock (_lock)
            return _fronts.TryGetValue(gameType, out Front? front) && faction is >= 0 and < Factions ? front.Scores[faction] : 0;
    }

    /// <summary>Who holds contract <paramref name="contract"/> of a game type; -1 for none.</summary>
    public int HolderOf(string gameType, int contract)
    {
        lock (_lock)
            return _fronts.TryGetValue(gameType, out Front? front) && contract >= 0 && contract < front.Holders.Length ? front.Holders[contract] : -1;
    }

    /// <summary>The sum of a modifier over the contracts a PMC holds in a game type (its contract bonus).</summary>
    public float ModifierOf(string gameType, int faction, string modifier)
    {
        if (!_bonuses.GameTypes.TryGetValue(gameType, out List<Dictionary<string, float>>? contracts))
            return 0;
        lock (_lock)
        {
            Front front = _fronts[gameType];
            float sum = 0;
            for (int c = 0; c < contracts.Count && c < front.Holders.Length; c++)
                if (front.Holders[c] == faction)
                    sum += contracts[c].GetValueOrDefault(modifier);
            return sum;
        }
    }

    /// <summary>
    /// A game of <paramref name="gameType"/> won by <paramref name="faction"/>:
    /// its bar moves up, and a full bar hands a contract over. Returns what
    /// changed hands, if anything (contract, from, to).
    /// </summary>
    public (int Contract, int From, int To)? RecordWin(string gameType, int faction, bool major)
    {
        (int, int, int)? moved = null;
        lock (_lock)
        {
            if (!_fronts.TryGetValue(gameType, out Front? front) || faction is < 0 or >= Factions)
                return null;
            front.Scores[faction] = Math.Min(_settings.BarTop, front.Scores[faction] + (major ? _settings.PointsPerMajorVictory : _settings.PointsPerMinorVictory));
            if (front.Scores[faction] >= _settings.BarTop)
            {
                moved = Takeover(front);
                Array.Clear(front.Scores);
            }
        }
        Save();
        return moved;
    }

    /// <summary>First place takes last place's contract, giving one back to second place first if it is at the limit.</summary>
    private (int, int, int)? Takeover(Front front)
    {
        int[] ranked = [.. Enumerable.Range(0, Factions).OrderByDescending(f => front.Scores[f]).ThenBy(f => f)];
        int first = ranked[0], second = ranked[1], last = ranked[^1];
        int Held(int faction) => front.Holders.Count(h => h == faction);
        // last place's own contract if it still holds it, else any it holds
        int taken = front.Holders.Length > last && front.Holders[last] == last ? last : Array.IndexOf(front.Holders, last);
        if (taken < 0)
            return null;
        if (Held(first) >= _settings.MaxContracts)
        {
            // give second place its own contract back if first holds it, else one first holds that isn't its own
            int returned = front.Holders.Length > second && front.Holders[second] == first ? second
                : Enumerable.Range(0, front.Holders.Length).FirstOrDefault(c => front.Holders[c] == first && c != first, -1);
            if (returned >= 0)
                front.Holders[returned] = second;
        }
        front.Holders[taken] = first;
        return (taken, last, first);
    }

    private static Dictionary<string, Front>? Load(string? path)
    {
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, Front>>(File.ReadAllText(path));
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[medius/mag] could not read {Path}; the Shadow War starts over", path);
            return null;
        }
    }

    private void Save()
    {
        if (_path is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        lock (_lock)
            File.WriteAllText(_path, JsonSerializer.Serialize(_fronts, new JsonSerializerOptions { WriteIndented = true }));
    }
}
