using Plugin.Medius.Extensions.Mag;

namespace Plugin.Medius.Tests;

/// <summary>A soldier's career stats: each game's stats summed per game type, the "...Max" stats kept as the best.</summary>
public sealed class MagCareerStatsTests
{
    [Fact]
    public void Games_add_up_per_game_type_and_maxima_keep_the_best()
    {
        MagStatTable table = MagStatTable.Embedded(MagClientVersion.Patch212);
        Assert.Equal(("Add", "Max", "Custom"), (table.Stats["EnemiesKilled"].Update, table.Stats["KillStreakMax"].Update, table.Stats["TotalExperience"].Update));
        MagCharacter soldier = new();
        soldier.RecordGame("GameType64", new Dictionary<string, long> { ["EnemiesKilled"] = 5, ["KillStreakMax"] = 3, ["BattlesCompleted"] = 1, ["TotalExperience"] = 900 }, table);
        soldier.RecordGame("GameType64", new Dictionary<string, long> { ["EnemiesKilled"] = 2, ["KillStreakMax"] = 2, ["BattlesCompleted"] = 1 }, table);
        soldier.RecordGame("GameType128", new Dictionary<string, long> { ["EnemiesKilled"] = 1, ["KillStreakMax"] = 4 }, table);
        Assert.Equal(7, soldier.CareerStats["GameType64"]["EnemiesKilled"]);
        Assert.Equal(3, soldier.CareerStats["GameType64"]["KillStreakMax"]);
        Assert.False(soldier.CareerStats["GameType64"].ContainsKey("TotalExperience"));   // Custom: the soldier's Experience
        Assert.Equal(8, soldier.CareerStat("EnemiesKilled", table));
        Assert.Equal(4, soldier.CareerStat("KillStreakMax", table));
        Assert.Equal(2, soldier.CareerStat("BattlesCompleted", table));
    }
}
