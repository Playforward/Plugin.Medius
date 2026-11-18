using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// MAG's Supply Depot (patch 2.00 on): ArmoryDataTable from
/// <c>scripts/armory.zpr</c>, each PMC's buyable items with their price,
/// level and item group, flattened by <c>tools/re/armory.py</c> and embedded
/// per client build as <c>Data/&lt;build&gt;/mag-armory.json</c>. 1.00 has no
/// Supply Depot and no table. The rules below are the client's lobby
/// emulator's (MagMediusEmulation, mage_g 2.12) [C].
/// </summary>
public sealed class MagArmory
{
    public const string FileName = "mag-armory.json";

    [JsonPropertyName("source")]
    public string Source { get; init; } = "";

    /// <summary>cash_refund_factor (ArmoryDataTable +0x30): the share of its price a refunded item gives back (0x6ff594).</summary>
    [JsonPropertyName("refundFactor")]
    public float RefundFactor { get; init; }

    /// <summary>per_level_minimum_cash_on_respec, kept for reference; what reads it isn't known.</summary>
    [JsonPropertyName("perLevelMinimumCashOnRespec")]
    public int PerLevelMinimumCashOnRespec { get; init; }

    /// <summary>ArmoryFactionClassData, one per PMC.</summary>
    [JsonPropertyName("factions")]
    public List<MagArmoryFaction> Factions { get; init; } = [];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, MagArmory?> EmbeddedTables = new();

    /// <summary>The build's Supply Depot, or null for a build without one.</summary>
    public static MagArmory? Embedded(MagClientVersion version) => EmbeddedTables.GetOrAdd(version.Protocol, _ =>
    {
        using Stream? stream = typeof(MagArmory).Assembly.GetManifestResourceStream(version.MediusResource(FileName));
        return stream is null ? null : JsonSerializer.Deserialize<MagArmory>(stream);
    });

    /// <summary>
    /// A PMC's entry for an item (0x7078a8: the PMC's equipment list,
    /// searched by item handle, 0x417bf4). The handle's low ten bits are the
    /// item's availableItems bit (0x417d20).
    /// </summary>
    public MagArmoryItem? Find(int faction, ushort handle) =>
        Factions.FirstOrDefault(f => f.Faction == faction)?.Items.FirstOrDefault(i => i.Bit == BitOf(handle));

    /// <summary>An item handle's availableItems bit (0x417d20): its low ten bits, -1 for a negative handle.</summary>
    public static int BitOf(ushort handle) => (short)handle < 0 ? -1 : handle & 0x3ff;

    /// <summary>
    /// The items an item's purchase or refund also gives or takes: when it is
    /// the parent of a group (group_parent names itself), every other item of
    /// the PMC naming it as parent (0x6ffaa4, the purchase's call passes 0, so
    /// only the parent carries its group) [C].
    /// </summary>
    public IEnumerable<MagArmoryItem> GroupOf(int faction, MagArmoryItem item) =>
        item.GroupParent.Length == 0 || item.GroupParent != item.Name
            ? []
            : Factions.FirstOrDefault(f => f.Faction == faction)?.Items.Where(i => i.GroupParent == item.Name && i.Name != item.Name) ?? [];
}

public sealed class MagArmoryFaction
{
    /// <summary>FactionType: Green (Valor) 0, Black (Raven) 1, Red (SVER) 2.</summary>
    [JsonPropertyName("faction")]
    public int Faction { get; init; }

    [JsonPropertyName("characterClass")]
    public string CharacterClass { get; init; } = "";

    [JsonPropertyName("items")]
    public List<MagArmoryItem> Items { get; init; } = [];
}

/// <summary>One equipment_list entry.</summary>
public sealed class MagArmoryItem
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>The item's availableItems bit (its handle's low ten bits).</summary>
    [JsonPropertyName("bit")]
    public int Bit { get; init; }

    /// <summary>Its price in CP; 0 for an item every soldier has, which can't be bought or refunded (price must be above 0) [C].</summary>
    [JsonPropertyName("price")]
    public uint Price { get; init; }

    /// <summary>The level the Supply Depot shows it from; the emulator doesn't check it.</summary>
    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("skillId")]
    public string SkillId { get; init; } = "";

    /// <summary>economy_types index: the Supply Depot tab.</summary>
    [JsonPropertyName("economyType")]
    public int EconomyType { get; init; }

    /// <summary>group_parent: the item whose purchase gives this one too; empty for none.</summary>
    [JsonPropertyName("groupParent")]
    public string GroupParent { get; init; } = "";
}
