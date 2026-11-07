using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using ServerShared.Titles;
using System.Text;

namespace Plugin.Medius.Tests;

/// <summary>
/// Resistance 2's title file (ServerApp-Titles/Resistance2) loads and says
/// what PSRewired's R2 server was seen to answer [live R2 1.60, MediusProxy 2026-10-29].
/// Skipped when the sibling ServerApp-Titles checkout is not there.
/// </summary>
public sealed class Resistance2TitleTests
{
    private static MediusTitle? Load()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "ServerApp-Titles", "Resistance2")))
            dir = Path.GetDirectoryName(dir);
        if (dir is null)
            return null;
        List<string> problems = [];
        List<MediusTitle> titles = MediusTitle.LoadAll(
            [new TitleDescriptor { TitleId = "resistance2", Name = "Resistance 2", ProtocolFamily = "medius", ResourceDir = Path.Combine(dir, "ServerApp-Titles", "Resistance2") }],
            problems);
        Assert.Empty(problems);
        return Assert.Single(titles);
    }

    [Fact]
    public void Resistance2_plays_on_its_own_udp_game_server()
    {
        MediusTitle? title = Load();
        if (title is null)
            return;
        Assert.Equal("3.03", title.RtProfile.Version);
        Assert.True(title.RtLayout.BinaryFwdOriginatorFirst);
        Assert.Equal(128, title.RtLayout.SvoUrlFieldLength);
        Assert.Equal(11001, title.GameServer.Port);
        Assert.Equal(NetConnectionType.NetConnectionType5, title.GameServer.ConnectionType);
        Assert.Equal(NetAddressType.NetAddressTypeBinaryExternal, title.GameServer.MatchAddressType);
        Assert.True(title.GameServer.ZeroServerKey);
        Assert.True(title.GameServer.HostSeat);
    }

    [Fact]
    public void Resistance2_pools_carry_the_games_option_sheet()
    {
        MediusTitle? title = Load();
        if (title is null)
            return;
        MediusMatchPool ctf = title.Matchmaking.Pools.Single(p => p.Name == "CTF");
        Assert.Equal(MediusGameHostType.MediusGameHostType5, ctf.HostType);

        // Every CTF game is one of CTF's menu regions; its sheet names that region's map and size.
        MediusMatchPool game = ctf.ForGame(new Random(1));
        Assert.Contains(ctf.Variants, v => v.Values["Region"] == game.Values["Region"] && v.MaxPlayers == game.MaxPlayers);
        byte[] sheet = game.GameOptionsParts[2].Build(new MediusMatch(null!, game, []));
        Assert.Equal($"Global\n,GameType,CTF\nCTF\n,Region,{game.Values["Region"]}\n,MaxPlayers,{game.MaxPlayers}\n,Map,{game.Values["Map"]}\n\0",
            Encoding.ASCII.GetString(sheet));

        // Ranked asks for any type (key 0000): its pool spans every competitive type, and no map pack map.
        MediusMatchPool any = title.Matchmaking.PoolFor(2, "0000");
        Assert.Equal(["CTF", "DeathMatch", "Meltdown", "Skirmish", "TeamDeathMatch"], any.Variants.Select(v => v.Values["GameType"]).Distinct().Order());
        Assert.DoesNotContain(any.Variants, v => v.Values["Map"] is "ART_CLASS_LEVEL_TWIN_FALLS_MULTIPLAYER" or "ART_CLASS_LEVEL_LUMBER_YARD_MULTIPLAYER");
        Assert.Equal("Competitive", title.Matchmaking.Default.Name);
    }

    [Fact]
    public void Resistance2_host_seat_settings_load()
    {
        MediusTitle? title = Load();
        if (title is null)
            return;
        Extensions.Resistance2.Resistance2Settings settings =
            System.Text.Json.JsonSerializer.Deserialize<Extensions.Resistance2.Resistance2Settings>(title.ExtensionSettings, MediusTitle.JsonOptions)!;
        Assert.Equal("R2 HOST", settings.Host.Name);
        Assert.Equal(0x39, settings.Host.StartRtMessage);
        Assert.Equal(0x84, settings.Host.FirstReplicatedObject);
        Assert.Equal(0x81, settings.Host.TableObject);
        Assert.Equal(0x82, settings.Host.MatchObject);
        Assert.Equal(8, settings.Host.MatchSlots);
    }
}
