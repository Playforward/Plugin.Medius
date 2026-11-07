using Plugin.Medius.Extensions.Mag;

namespace Plugin.Medius.Tests;

/// <summary>The Shadow War: score bars per game type and PMC, and the contracts a full bar takes (wiki Contracts).</summary>
public sealed class MagShadowWarTests
{
    private static MagShadowWar War() => new(MagContractBonuses.Embedded(MagClientVersion.Patch212), new MagShadowWarSettings());

    [Fact]
    public void Each_pmc_starts_with_its_own_contract_and_sabotages_contracts_add_experience()
    {
        MagContractBonuses bonuses = MagContractBonuses.Embedded(MagClientVersion.Patch212);
        Assert.Equal(["GameType64", "GameType_96_Sabotage", "GameType128", "GameTypeInterdiction", "GameType256"], bonuses.GameTypes.Keys);
        MagShadowWar war = War();
        Assert.Equal([0, 1, 2], Enumerable.Range(0, 3).Select(c => war.HolderOf("GameType64", c)));
        Assert.Equal(0.05f, war.ModifierOf("GameType64", 1, MagContractBonuses.ExperienceModifier), 3);
        Assert.Equal(0f, war.ModifierOf("GameType128", 1, MagContractBonuses.ExperienceModifier));   // Acquisition's is the leadership aura
    }

    [Fact]
    public void A_full_bar_takes_last_places_contract_and_starts_the_bars_again()
    {
        MagShadowWar war = War();
        war.RecordWin("GameType64", 1, major: false);                     // Raven +5
        for (int i = 0; i < 9; i++)
            Assert.Null(war.RecordWin("GameType64", 0, major: true));    // Valor +10 each
        Assert.Equal((90, 5, 0), (war.ScoreOf("GameType64", 0), war.ScoreOf("GameType64", 1), war.ScoreOf("GameType64", 2)));
        Assert.Equal((2, 2, 0), war.RecordWin("GameType64", 0, major: true));   // SVER is last: Valor takes its contract
        Assert.Equal([0, 1, 0], Enumerable.Range(0, 3).Select(c => war.HolderOf("GameType64", c)));
        Assert.Equal(0, war.ScoreOf("GameType64", 0));
        Assert.Equal(0.1f, war.ModifierOf("GameType64", 0, MagContractBonuses.ExperienceModifier), 3);
    }

    [Fact]
    public void A_pmc_at_the_limit_gives_one_back_and_a_last_place_with_none_loses_nothing()
    {
        MagShadowWar war = War();
        for (int i = 0; i < 10; i++)
            war.RecordWin("GameType64", 0, major: true);                   // Valor takes SVER's
        war.RecordWin("GameType64", 2, major: false);                      // SVER isn't last any more
        for (int i = 0; i < 10; i++)
            war.RecordWin("GameType64", 0, major: true);                   // Raven (0) is last
        // Valor held 2: SVER's goes back to second place (SVER), then Valor takes Raven's
        Assert.Equal([0, 0, 2], Enumerable.Range(0, 3).Select(c => war.HolderOf("GameType64", c)));
        war.RecordWin("GameType64", 2, major: false);                      // the bars started again: put Raven last
        for (int i = 0; i < 10; i++)
            war.RecordWin("GameType64", 0, major: true);                   // Raven, last, holds none
        Assert.Equal([0, 0, 2], Enumerable.Range(0, 3).Select(c => war.HolderOf("GameType64", c)));
        Assert.Equal(0, war.ScoreOf("GameType64", 0));                     // the bars still start again
    }

    [Fact]
    public void The_war_is_kept_across_restarts()
    {
        string path = Path.Combine(Path.GetTempPath(), $"mag-war-{Guid.NewGuid():N}.json");
        try
        {
            MagShadowWar war = new(MagContractBonuses.Embedded(MagClientVersion.Patch212), new MagShadowWarSettings(), path);
            war.RecordWin("GameType256", 2, major: true);
            MagShadowWar again = new(MagContractBonuses.Embedded(MagClientVersion.Patch212), new MagShadowWarSettings(), path);
            Assert.Equal(10, again.ScoreOf("GameType256", 2));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
