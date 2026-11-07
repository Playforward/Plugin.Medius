using ServerShared.Titles;

namespace Plugin.Medius.Tests;

/// <summary>
/// Resistance: Fall of Man's title file (ServerApp-Titles/ResistanceFallOfMan) loads
/// with the values read from its disc EBOOT [C]. Skipped when the sibling
/// ServerApp-Titles checkout is not there.
/// </summary>
public sealed class ResistanceFallOfManTitleTests
{
    private static MediusTitle? Load()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "ServerApp-Titles", "ResistanceFallOfMan")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            return null;
        List<string> problems = [];
        List<MediusTitle> titles = MediusTitle.LoadAll(
            [new TitleDescriptor { TitleId = "resistance", Name = "Resistance: Fall of Man", ProtocolFamily = "medius", ResourceDir = Path.Combine(dir, "ServerApp-Titles", "ResistanceFallOfMan") }],
            problems);
        Assert.Empty(problems);
        return Assert.Single(titles);
    }

    [Fact]
    public void Fall_of_Man_loads_with_the_patchs_profile_and_its_region_app_ids()
    {
        MediusTitle? title = Load();
        if (title is null)
            return;
        Assert.Same(ScertLibraryProfile.Rt302, title.RtProfile);
        Assert.True(title.RtLayout.SupersetStatusCodeRepeated);
        Assert.Equal("resistance", title.Extension);
        Assert.Equal(8, title.Matchmaking.Supersets.Count);
        Assert.Equal([0, 0, 1, 0], System.Text.Encoding.UTF8.GetBytes(title.Matchmaking.Supersets[0].ExtraInfo));
        MediusUniverse universe = Assert.Single(title.Universes);
        Assert.StartsWith("8.7.1.1 http://{PublicHost}", universe.ExtendedInfo);
        Assert.Equal(1, universe.Status);
        Assert.Equal(128, title.RtLayout.UniverseExtendedInfoLength);
        Assert.Equal(16, title.RtLayout.DmeServerVersionLength);
        Assert.Equal([20174, 20724, 20734, 20744], title.AppIds);
    }
}
