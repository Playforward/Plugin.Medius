using Plugin.Medius.Extensions.Mag;

namespace Plugin.Medius.Tests;

/// <summary>
/// The embedded skill table (from the retail disc) and the client's
/// progression rules applied to a <see cref="MagCharacter"/>.
/// </summary>
public sealed class MagSkillTableTests
{
    private static readonly MagSkillTable Table = MagSkillTable.Embedded(MagClientVersion.Retail);
    private static readonly MagRules Rules = new(new MagSettings(), Table);

    [Fact]
    public void Embedded_table_is_the_retail_one()
    {
        Assert.Equal(98, Table.Skills.Count);
        Assert.Equal(60, Table.MaxLevel);
        Assert.Equal(new uint[] { 3000, 5000, 10000, 17000, 26000 }, Table.ReSpecPoints);
        Assert.Equal(87u, Table.ExperienceForLevel(2));
        Assert.Equal(64738u, Table.ExperienceForLevel(60));
        Assert.Equal(1, Table.SkillPointsGained(1));

        MagSkill recruit = Table.Skills[0];
        Assert.Equal((0, 0, "kSkillCategoryAssault"), (recruit.Tier, recruit.Cost, recruit.Category));
        // Skill 1: tier 2 Assault, 3 points, a different rifle per PMC.
        MagSkill rifle = Table.Skills[1];
        Assert.Equal((2, 3), (rifle.Tier, rifle.Cost));
        Assert.Equal(["scar_l"], rifle.Factions[0].Items.Select(i => i.Name));
        Assert.Equal(["f2000", "foregrip_f2000"], rifle.Factions[1].Items.Select(i => i.Name));
        Assert.Equal(["an_94"], rifle.Factions[2].Items.Select(i => i.Name));
    }

    /// <summary>FactionTypeAll items are bit = index; a PMC list's items are bit 50 + index.</summary>
    [Fact]
    public void Item_bits_follow_the_item_list()
    {
        Assert.Equal("c4", Table.ItemLists[0][13]);
        Assert.Equal(13, Table.Skills[0].Factions[0].Items.Single(i => i.Name == "c4").Bit);
        Assert.Equal("knife_green", Table.ItemLists[1][3]);
        Assert.Equal(53, Table.Skills[0].Factions[0].Items.Single(i => i.Name == "knife_green").Bit);
        Assert.Equal(50, Table.Skills[0].Factions[1].Items.Single(i => i.Name == "knife_black").Bit);
        // Commented out of the item list: the client finds no bit and skips it.
        Assert.Equal(-1, Table.Skills[52].Factions[0].Items.Single().Bit);
    }

    [Fact]
    public void New_soldier_gets_the_recruit_kit_of_its_pmc()
    {
        MagCharacter valor = NewSoldier(faction: 0);
        Assert.True(valor.HasSkill(0));
        Assert.Equal(1, valor.SkillsPurchased);
        Assert.Equal(Rules.StartingSkillPoints, valor.SkillPoints);   // the recruit skill is free
        Assert.Equal(11, valor.SkillPoints);                          // level 1's point + StartingSkillPoints
        Assert.Equal(KitBits(0, 0), SetBits(valor));

        MagCharacter raven = NewSoldier(faction: 1);
        Assert.Equal(KitBits(0, 1), SetBits(raven));
        Assert.NotEqual(SetBits(valor), SetBits(raven));
        Assert.Equal(MagSkillTable.DefaultSpecialty, raven.SkillSpecialty);
    }

    [Fact]
    public void Purchase_checks_faction_cost_and_previous_tier()
    {
        MagCharacter soldier = NewSoldier(faction: 0);

        Assert.False(soldier.TryPurchaseSkill(1, Rules, out string reason));
        Assert.Contains("previous tier", reason);

        Assert.True(soldier.TryPurchaseSkill(37, Rules, out _));   // tier 1 Assault, 2 points
        Assert.Equal(9, soldier.SkillPoints);
        Assert.Contains(107, SetBits(soldier));                     // foregrip_1

        Assert.True(soldier.TryPurchaseSkill(1, Rules, out _));    // 2 points spent in tier 1 now
        Assert.Equal(6, soldier.SkillPoints);
        Assert.Contains(56, SetBits(soldier));                      // scar_l

        Assert.False(soldier.TryPurchaseSkill(52, Rules, out reason));
        Assert.Contains("not available", reason);
        Assert.False(soldier.TryPurchaseSkill(98, Rules, out reason));
        Assert.Equal("invalid skill id", reason);
        Assert.False(soldier.TryPurchaseSkill(37, Rules, out reason));
        Assert.Equal("skill already purchased", reason);

        soldier.SkillPoints = 1;
        Assert.False(soldier.TryPurchaseSkill(38, Rules, out reason));
        Assert.Contains("not enough skill points", reason);
    }

    /// <summary>The recruit skill weighs 1 for Commando; one sniper skill ties it and the lower specialty wins.</summary>
    [Fact]
    public void Specialty_follows_the_skills()
    {
        MagCharacter soldier = NewSoldier(faction: 2);
        Assert.True(soldier.TryPurchaseSkill(20, Rules, out _));   // tier 1 Marksman, kSkillSpecialtySniper
        Assert.Equal(2, soldier.SkillSpecialty);
    }

    [Fact]
    public void Skill_respec_costs_by_count_and_gives_the_kit_back()
    {
        MagCharacter soldier = NewSoldier(faction: 0);
        Assert.True(soldier.TryPurchaseSkill(37, Rules, out _));
        Assert.True(soldier.TryPurchaseSkill(1, Rules, out _));
        Assert.Equal(3000u, soldier.RespecPoints);

        Assert.True(soldier.TrySkillReSpec(Rules, out _));
        Assert.Equal(11, soldier.SkillPoints);
        Assert.Equal(1, soldier.SkillsPurchased);
        Assert.True(soldier.HasSkill(0));
        Assert.Equal(KitBits(0, 0), SetBits(soldier));
        Assert.Equal(1, soldier.RespecCount);
        Assert.Equal(0u, soldier.RespecPoints);

        soldier.RespecPoints = 4999;
        Assert.False(soldier.TrySkillReSpec(Rules, out string reason));
        Assert.Contains("needs 5000", reason);
        soldier.RespecCount = 200;   // past the table: the last requirement
        soldier.RespecPoints = 26000;
        Assert.True(soldier.TrySkillReSpec(Rules, out _));
    }

    [Fact]
    public void Faction_respec_swaps_the_kit()
    {
        MagCharacter soldier = NewSoldier(faction: 0);
        Assert.True(soldier.TryPurchaseSkill(37, Rules, out _));

        Assert.True(soldier.TryFactionReSpec(1, Rules, out _));
        Assert.Equal(11, soldier.SkillPoints);
        Assert.Equal(KitBits(0, 1), SetBits(soldier));
        Assert.Equal(1 | 2, soldier.FactionHistory);
    }

    [Fact]
    public void Veteran_respec_starts_over_at_level_one()
    {
        MagCharacter soldier = NewSoldier(faction: 0);
        Assert.False(soldier.TryVeteranReSpec(2, Rules, out _));

        soldier.Level = 60;
        soldier.Experience = 70000;
        soldier.SkillPoints = 40;
        soldier.RespecCount = 3;
        Assert.True(soldier.TryPurchaseSkill(37, Rules, out _));
        Assert.True(soldier.TryVeteranReSpec(2, Rules, out _));
        Assert.Equal((1, 0u, 1, 0), (soldier.Level, soldier.Experience, soldier.VeteranLevel, soldier.RespecCount));
        Assert.Equal(11, soldier.SkillPoints);
        Assert.Equal(KitBits(0, 2), SetBits(soldier));
        Assert.Equal(MagCharacter.FlagFactionChanged, soldier.Flags);
    }

    /// <summary>Without a table every skill costs the flat setting and nothing is unlocked or granted.</summary>
    [Fact]
    public void Settings_stand_in_without_a_table()
    {
        MagRules flat = new(new MagSettings { SkillCost = 2, SkillReSpecCost = 1, StartingSkillPoints = 5 }, null);
        MagCharacter soldier = new() { Faction = 0, SkillPoints = flat.StartingSkillPoints, RespecPoints = 1 };
        Assert.False(soldier.GrantStartingSkills(flat));
        Assert.Equal(5, soldier.SkillPoints);

        Assert.True(soldier.TryPurchaseSkill(1, flat, out _));
        Assert.True(soldier.TryPurchaseSkill(52, flat, out _));
        Assert.Equal(1, soldier.SkillPoints);
        Assert.Empty(SetBits(soldier));

        Assert.True(soldier.TrySkillReSpec(flat, out _));
        Assert.Equal(5, soldier.SkillPoints);
        Assert.False(soldier.TrySkillReSpec(flat, out _));
        Assert.Null(MagRules.LoadTable(new MagSettings { SkillTable = "none" }, ""));
    }

    /// <summary>
    /// StartingLevel as the emulator's create applies it (0x8a4e14): the
    /// table's experience for the level and AwardLevel's skill points for
    /// every level up to it, on top of the usual starting points.
    /// </summary>
    [Fact]
    public void Starting_level_ten_opens_every_mode()
    {
        MagRules rules = new(System.Text.Json.JsonSerializer.Deserialize<MagSettings>("""{ "StartingLevel": 10 }""")!, Table);
        Assert.Equal(10, rules.StartingLevel);
        MagCharacter soldier = new() { Faction = 1, SkillPoints = rules.StartingSkillPoints };
        Assert.True(soldier.RaiseToLevel(rules.StartingLevel, rules));
        Assert.Equal(10, soldier.Level);
        Assert.Equal(Table.ExperienceForLevel(10), soldier.Experience);
        Assert.True(soldier.Experience > 0);
        int gained = Enumerable.Range(2, 9).Sum(Table.SkillPointsGained);
        Assert.True(gained > 0);
        Assert.Equal(rules.StartingSkillPoints + gained, soldier.SkillPoints);
        Assert.False(soldier.RaiseToLevel(5, rules));              // never lowers
        Assert.Equal(10, soldier.Level);
    }

    [Fact]
    public void Starting_level_is_clamped_and_one_is_retail()
    {
        Assert.Equal(1, new MagRules(new MagSettings(), Table).StartingLevel);
        MagRules high = new(new MagSettings { StartingLevel = 200 }, Table);
        Assert.Equal(Table.MaxLevel, high.StartingLevel);
        MagCharacter soldier = new() { SkillPoints = 250 };
        soldier.RaiseToLevel(high.StartingLevel, high);
        Assert.Equal(60, soldier.Level);
        Assert.Equal(byte.MaxValue, soldier.SkillPoints);           // clamped like the client's adder (0x8a82e8)
        MagCharacter fresh = NewSoldier(faction: 0);
        Assert.False(fresh.RaiseToLevel(Rules.StartingLevel, Rules));
        Assert.Equal(1, fresh.Level);
    }

    /// <summary>Battle experience adds up and takes the soldier past every level threshold it reaches, with each level's skill points.</summary>
    [Fact]
    public void Battle_experience_levels_the_soldier_up()
    {
        MagCharacter soldier = NewSoldier(faction: 0);
        byte points = soldier.SkillPoints;
        uint toLevel3 = Table.ExperienceForLevel(3);
        Assert.Equal(0, soldier.AddExperience(Table.ExperienceForLevel(2) - 1, Rules));
        Assert.Equal(1, soldier.Level);
        Assert.Equal(2, soldier.AddExperience(toLevel3 - soldier.Experience, Rules));
        Assert.Equal((3, toLevel3), (soldier.Level, soldier.Experience));
        Assert.Equal(points + Table.SkillPointsGained(2) + Table.SkillPointsGained(3), soldier.SkillPoints);
    }

    private static MagCharacter NewSoldier(sbyte faction)
    {
        MagCharacter soldier = new()
        {
            Faction = faction,
            FactionHistory = (byte)(1 << faction),
            SkillPoints = Rules.StartingSkillPoints,
            RespecPoints = new MagSettings().StartingRespecPoints,
        };
        Assert.True(soldier.GrantStartingSkills(Rules));
        return soldier;
    }

    private static int[] KitBits(int skill, int faction) =>
        [.. Table.Skills[skill].Factions[faction].Items.Select(i => i.Bit).Where(b => b >= 0).Distinct().Order()];

    private static int[] SetBits(MagCharacter soldier) =>
        [.. Enumerable.Range(0, MagCharacter.ItemWords * 32).Where(b => (soldier.AvailableItems[b >> 5] & (1u << (b & 31))) != 0)];
}
