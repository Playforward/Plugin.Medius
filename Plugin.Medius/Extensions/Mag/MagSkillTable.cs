using System.Text.Json;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// MAG's character progression data: MagCharacterSkillTable, the levels of
/// ExperienceTable and the item list the skills unlock from. The client does
/// not compile these in; it loads them from <c>scripts/character_skills.zpr</c>,
/// <c>experience.zpr</c> and <c>item_list.zpr</c> in the disc's
/// <c>game.psarc.sdat</c> (singletons at mage_g 0x8b6c00 and 0x8e4068).
/// <c>Data/mag-character-tables.json</c> is those three files flattened by
/// the extractor, with each skill's item names already turned into
/// availableItems bits, and is embedded in the plugin. The rules below are
/// the client emulator's (MagMediusEmulation), with addresses [C].
/// </summary>
public sealed class MagSkillTable
{
    public const string FileName = "mag-character-tables.json";

    /// <summary>What getSkillSpecialty (0x8a2d44) answers before any skill outweighs it: Commando.</summary>
    public const byte DefaultSpecialty = 4;

    public string Source { get; set; } = "";
    public double ExperienceRequiredScale { get; set; } = 1;
    /// <summary>The level count minus one (0x8df690); entry 0 of <see cref="Levels"/> is unused.</summary>
    public int MaxLevel { get; set; }
    public List<MagExperienceLevel> Levels { get; set; } = [];
    /// <summary>MagCharacterSkillReSpec: the respec points the n-th skill respec needs.</summary>
    public List<uint> ReSpecPoints { get; set; } = [];
    public List<MagSkillTier> Tiers { get; set; } = [];
    /// <summary>By skill id: the id is the index (GetSkill, 0x8b41c8); retired skills stay as empty entries.</summary>
    public List<MagSkill> Skills { get; set; } = [];
    /// <summary>ItemList.factionItemLists (All, Green, Black, Red) by index, for reference; null where an id is commented out.</summary>
    public List<List<string?>> ItemLists { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static MagSkillTable Read(Stream stream) =>
        JsonSerializer.Deserialize<MagSkillTable>(stream, Options) ?? throw new InvalidDataException("empty MAG skill table");

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, MagSkillTable> EmbeddedTables = new();

    /// <summary>
    /// The table of <paramref name="version"/>'s game data, embedded in this
    /// assembly (<c>Data/&lt;version folder&gt;/mag-character-tables.json</c>;
    /// 1.00 from game.psarc.sdat, 2.12 from the patch's tpps.psarc.sdat over it).
    /// </summary>
    public static MagSkillTable Embedded(MagClientVersion version) => EmbeddedTables.GetOrAdd(version.Protocol, _ =>
    {
        string name = version.MediusResource(FileName);
        using Stream stream = typeof(MagSkillTable).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded");
        return Read(stream);
    });

    public MagSkill? GetSkill(int id) => id >= 0 && id < Skills.Count ? Skills[id] : null;

    /// <summary>The skill's block for the soldier's PMC (Valor, Raven, SVER = 0, 1, 2), or null.</summary>
    private static MagSkillFaction? FactionOf(MagSkill skill, sbyte faction) =>
        faction >= 0 && faction < skill.Factions.Count ? skill.Factions[faction] : null;

    /// <summary>isAvailable (0x8b1a20): the PMC's Available flag; false for any other faction value.</summary>
    public static bool IsAvailable(MagSkill skill, sbyte faction) => FactionOf(skill, faction)?.Available ?? false;

    /// <summary>
    /// The items buying the skill unlocks for a PMC (0x8b20d4: the PMC's
    /// Equipment, or the skill's Default block's when that is empty; the
    /// extractor has already made that choice).
    /// </summary>
    public static IReadOnlyList<MagSkillItem> Items(MagSkill skill, sbyte faction) => FactionOf(skill, faction)?.Items ?? [];

    /// <summary>
    /// A soldier's skill modifiers: the modifiers of every skill they have
    /// (free ones included, which are bought into the record), for their PMC,
    /// added up by name. Most scale a base value by (1 + sum) (1.00 0x939da8,
    /// the incapacitated time); kCharModResuscitatePercent is the fraction of
    /// health a revive gives.
    /// </summary>
    public Dictionary<string, double> Modifiers(Func<int, bool> hasSkill, sbyte faction)
    {
        Dictionary<string, double> sum = new(StringComparer.Ordinal);
        foreach (MagSkill skill in Skills)
        {
            if (!hasSkill(skill.Id) || FactionOf(skill, faction) is not { } block)
                continue;
            foreach ((string name, double value) in block.Modifiers)
                sum[name] = sum.GetValueOrDefault(name) + value;
        }
        return sum;
    }

    /// <summary>
    /// GetTierPrereqPoints (0x8b3d78): the points that must be spent in tier
    /// <paramref name="tier"/> - 1 of the same category first. Only tiers 2..5
    /// have a value; a category without tier data needs nothing.
    /// </summary>
    public int TierPrereqPoints(string? category, sbyte faction, int tier)
    {
        if (tier is < 2 or > 5 || faction is < 0 or > 2)
            return 0;
        MagSkillTier? data = Tiers.FirstOrDefault(t => t.Category == category);
        return data is not null && faction < data.PrereqPoints.Count && tier - 2 < data.PrereqPoints[faction].Count
            ? data.PrereqPoints[faction][tier - 2]
            : 0;
    }

    /// <summary>The respec requirement for the next skill respec (0x8b2140): past the last entry the last one applies.</summary>
    public uint? ReSpecCost(int respecCount) =>
        respecCount < 0 || ReSpecPoints.Count == 0 ? null : ReSpecPoints[Math.Min(respecCount, ReSpecPoints.Count - 1)];

    /// <summary>The skill points AwardLevel (0x8a46bc) gives for reaching <paramref name="level"/>.</summary>
    public int SkillPointsGained(int level) => level > 0 && level < Levels.Count ? Levels[level].SkillPointsGained : 0;

    /// <summary>The experience a level starts at (0x8e3ffc): the table's value times ExperienceRequiredScale.</summary>
    public uint ExperienceForLevel(int level) =>
        level > 0 && level < Levels.Count ? (uint)(int)(float)(Levels[level].Experience * (float)ExperienceRequiredScale) : 0;

    /// <summary>
    /// getSkillSpecialty (0x8a2d44): adds up each purchased skill's
    /// SpecialtyWeight per specialty in id order; the leader changes when a
    /// specialty passes it, or ties it with a lower value.
    /// </summary>
    public byte SkillSpecialty(Func<int, bool> hasSkill)
    {
        int[] weights = new int[5];
        int best = DefaultSpecialty;
        foreach (MagSkill skill in Skills)
        {
            if (!hasSkill(skill.Id) || skill.Specialty is < 0 or > 4)
                continue;
            weights[skill.Specialty] += skill.SpecialtyWeight;
            if (weights[skill.Specialty] > weights[best] || (weights[skill.Specialty] == weights[best] && skill.Specialty < best))
                best = skill.Specialty;
        }
        return (byte)best;
    }
}

public sealed class MagExperienceLevel
{
    public int Level { get; set; }
    public uint Experience { get; set; }
    public int SkillPointsGained { get; set; }
    public int LoadoutBudget { get; set; }
    public int Rank { get; set; }
}

public sealed class MagSkillTier
{
    public string Category { get; set; } = "";
    /// <summary>By PMC (Green, Black, Red): Tier2..Tier5_PrereqPoints.</summary>
    public List<List<int>> PrereqPoints { get; set; } = [];
}

/// <summary>One MagCharacterSkill (0xfc bytes in the client).</summary>
public sealed class MagSkill
{
    public int Id { get; set; }
    /// <summary>1.00 +0x04. Tier 0 skills are the free ones every soldier gets (0x8a3330); null in 2.x, which has no tiers.</summary>
    public int? Tier { get; set; }
    /// <summary>2.x: the SKILL_ID_* name.</summary>
    public string? SkillId { get; set; }
    /// <summary>2.x: the skill that must be bought first (CanPurchaseSkill 0x6fa084, skill +0xc), by id.</summary>
    public int? Parent { get; set; }
    /// <summary>2.x: a further skill that must be bought first (skill +0x4), by id.</summary>
    public int? Prerequisite { get; set; }
    /// <summary>2.x: the level needed, when 2 or more (skill +0x10 against record +0x40).</summary>
    public int? LevelPrerequisite { get; set; }
    /// <summary>2.x: the skill's rank in its line, 1..3; 0 for SKILL_ID_BasicDefault, the free one.</summary>
    public int? SkillLevel { get; set; }

    /// <summary>A skill every soldier gets for free: tier 0 in 1.00, skillLevel 0 in 2.x.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsStarting => Tier == 0 || SkillLevel == 0;
    public string? Group { get; set; }
    /// <summary>+0x0c, compared for the tier prerequisite.</summary>
    public string? Category { get; set; }
    /// <summary>+0x10, kSkillSpecialty: FieldSupport, RapidAssault, Sniper, DirectAction, Commando = 0..4; -1 when unset.</summary>
    public int Specialty { get; set; } = -1;
    /// <summary>+0x14.</summary>
    public int SpecialtyWeight { get; set; }
    /// <summary>+0x18, in skill points.</summary>
    public int Cost { get; set; }
    public string? Icon { get; set; }
    /// <summary>By PMC: Green (Valor), Black (Raven), Red (SVER).</summary>
    public List<MagSkillFaction> Factions { get; set; } = [];
}

public sealed class MagSkillFaction
{
    public bool Available { get; set; }
    public string Name { get; set; } = "";
    public List<MagSkillItem> Items { get; set; } = [];
    /// <summary>What the skill changes for a soldier of this PMC: kCharMod name → value (the block's Modifiers, else the Default block's).</summary>
    public Dictionary<string, double> Modifiers { get; set; } = [];
}

/// <summary>
/// An item a skill unlocks and its availableItems bit (0x4b9944): an item of
/// the FactionTypeAll list is bit = its index, one of a PMC list bit = 50 +
/// its index. -1 when the item list does not have it; the client skips it.
/// </summary>
public sealed class MagSkillItem
{
    public string Name { get; set; } = "";
    public int Bit { get; set; } = -1;
}

/// <summary>
/// The progression rules a MAG server applies: the skill table when it is
/// there, and the <see cref="MagSettings"/> stand-ins when it is not.
/// </summary>
public sealed class MagRules(MagSettings settings, MagSkillTable? table)
{
    public MagSettings Settings => settings;
    public MagSkillTable? Table => table;

    /// <summary>A skill's cost: the table's, or the flat <see cref="MagSettings.SkillCost"/>.</summary>
    public int SkillCost(int skill) => table is null ? settings.SkillCost : table.GetSkill(skill)?.Cost ?? 0;

    /// <summary>The respec points the next skill respec needs; null when the table has no requirement for it.</summary>
    public uint? ReSpecCost(int respecCount) => table is null ? settings.SkillReSpecCost : table.ReSpecCost(respecCount);

    public int MaxLevel => table?.MaxLevel ?? settings.MaxLevel;

    /// <summary>
    /// A level 1 soldier's skill points: AwardLevel(1) (0x8a46bc, called for
    /// levels 1..StartingLevel at creation) plus <see cref="MagSettings.StartingSkillPoints"/>,
    /// the emulator's AdditionalSkillPoints.
    /// </summary>
    public byte StartingSkillPoints => (byte)Math.Min((table?.SkillPointsGained(1) ?? 0) + settings.StartingSkillPoints, byte.MaxValue);

    /// <summary><see cref="MagSettings.StartingLevel"/>, between 1 and <see cref="MaxLevel"/>.</summary>
    public int StartingLevel => Math.Clamp((int)settings.StartingLevel, 1, Math.Max(1, MaxLevel));

    /// <summary>The skill points AwardLevel gives for reaching <paramref name="level"/> (none without a table).</summary>
    public int SkillPointsGained(int level) => table?.SkillPointsGained(level) ?? 0;

    /// <summary>The experience a level starts at (none without a table).</summary>
    public uint ExperienceForLevel(int level) => table?.ExperienceForLevel(level) ?? 0;

    /// <summary>
    /// The table named by <see cref="MagSettings.SkillTable"/>: the embedded
    /// one by default, a file (relative to the title's resource directory)
    /// when a path is given, none for <c>"none"</c> or when it cannot be read.
    /// </summary>
    public static MagSkillTable? LoadTable(MagSettings settings, string resourceDir)
    {
        string name = settings.SkillTable.Trim();
        if (name.Equals("none", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            if (name.Length == 0)
                return MagSkillTable.Embedded(settings.Version);
            using FileStream file = File.OpenRead(Path.Combine(resourceDir, name));
            return MagSkillTable.Read(file);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[medius/mag] could not load the skill table {Name}; every skill costs {Cost}", name.Length == 0 ? "(embedded)" : name, settings.SkillCost);
            return null;
        }
    }
}
