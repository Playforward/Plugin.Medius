using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// One build's character stats as the lobby keeps and serves them, embedded as
/// <c>Data/&lt;version folder&gt;/mag-char-stat-info.json</c> by
/// <c>tools/re/charstatinfo.py</c> (in Plugin.MagGameServer): each stat's id
/// (CharacterStatType), its SVO name (the stats pages' name for it,
/// character_stats.zpr's SVO) and how a game adds to a career (UpdateType).
/// </summary>
public sealed class MagStatTable
{
    public const string FileName = "mag-char-stat-info.json";

    [JsonPropertyName("stats")]
    public Dictionary<string, MagStatInfo> Stats { get; init; } = [];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, MagStatTable> EmbeddedTables = new();

    /// <summary>The table of <paramref name="version"/>'s client, embedded in this assembly.</summary>
    public static MagStatTable Embedded(MagClientVersion version) => EmbeddedTables.GetOrAdd(version.Protocol, _ =>
    {
        string name = version.MediusResource(FileName);
        using Stream stream = typeof(MagStatTable).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded");
        return JsonSerializer.Deserialize<MagStatTable>(stream) ?? new();
    });

    /// <summary>kStatUpdateAdd: a career sums its games.</summary>
    public const string Add = "Add";
    /// <summary>kStatUpdateMax: a career keeps its best game.</summary>
    public const string Max = "Max";

    /// <summary>How a game adds to a career for <paramref name="stat"/>: Add, Max, or null when it isn't kept that way (Custom, None, or not a stat of this build).</summary>
    public string? UpdateOf(string stat) =>
        Stats.TryGetValue(stat, out MagStatInfo? info) && info.Update is Add or Max ? info.Update : null;
}

public sealed class MagStatInfo
{
    [JsonPropertyName("id")] public int Id { get; init; }
    /// <summary>The stats pages' name for the stat (e.g. "ENEMIES KILLED"); null for a stat the pages don't carry.</summary>
    [JsonPropertyName("svo")] public string? Svo { get; init; }
    /// <summary>kStatUpdate* without the prefix: Add, Max, Custom, None.</summary>
    [JsonPropertyName("update")] public string? Update { get; init; }
    /// <summary>kStatDisplay* without the prefix: Int, Float, Time, None.</summary>
    [JsonPropertyName("display")] public string? Display { get; init; }
}
