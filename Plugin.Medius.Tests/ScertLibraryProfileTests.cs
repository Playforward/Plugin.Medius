using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;
using ServerShared.Titles;
using System.Text;

namespace Plugin.Medius.Tests;

/// <summary>
/// Layouts chosen by a title's <see cref="MediusTitle.RtVersion"/>, checked
/// against the SCE-RT 3.03 client's own sizes (Resistance 2 EBOOT; addresses
/// in ScertLibraryProfile.cs and docs/RT-VERSIONS.md).
/// </summary>
public sealed class ScertLibraryProfileTests
{
    // Resistance 2's rt_msg_client ReleaseVersion.
    private const string R2RtVersion = "3.03.01.200809171107";

    // Resistance: Fall of Man's (disc 1.00) rt_msg_client ReleaseVersion.
    private const string RfomRtVersion = "3.98.200610022359";

    // Medius 112+ is where Horizon adds MediusJoinGameResponse's 4 trailing bytes.
    private const int MediusVersion = 113;

    private static MediusTitle Title(string rtVersion) => new() { RtVersion = rtVersion };

    /// <summary>The app message as RT_MSG_SERVER_APP carries it: class, type, then the struct.</summary>
    private static byte[] AppBytes(BaseMediusMessage message)
    {
        using MemoryStream stream = new();
        using (MessageWriter writer = new(stream) { MediusVersion = MediusVersion })
            new RT_MSG_SERVER_APP { Message = message }.Serialize(writer);
        return stream.ToArray();
    }

    [Theory]
    [InlineData("3.03")]
    [InlineData(R2RtVersion)]
    [InlineData("3.03.01.200811271300")] // Killzone 2
    public void Version_line_selects_the_303_profile(string rtVersion)
    {
        Assert.True(ScertLibraryProfile.TryFind(rtVersion, out ScertLibraryProfile profile));
        Assert.Same(ScertLibraryProfile.Rt303, profile);
        Assert.Equal(new ScertLayout
        {
            UniverseExtendedInfoLength = 256,
            UniversePadAfterEndOfList = false,
            JoinGameTrailingPadLength = 0,
            DmeServerVersionLength = 20,
            SvoUrlFieldLength = 128,
            BinaryFwdOriginatorFirst = true,
        }, Title(rtVersion).RtLayout);
    }

    [Fact]
    public void No_version_keeps_Horizons_layouts()
    {
        Assert.True(ScertLibraryProfile.TryFind("", out ScertLibraryProfile profile));
        Assert.Same(ScertLibraryProfile.Default, profile);
        Assert.Equal(new ScertLayout
        {
            UniverseExtendedInfoLength = Constants.UNIVERSE_EXTENDED_INFO_MAXLEN,
            UniversePadAfterEndOfList = true,
            JoinGameTrailingPadLength = null,
            DmeServerVersionLength = Constants.DME_VERSION_LENGTH,
        }, new MediusTitle().RtLayout);
    }

    [Fact]
    public void Unknown_version_finds_no_profile()
    {
        Assert.False(ScertLibraryProfile.TryFind("9.99.00.000000000000", out _));
        Assert.Same(ScertLibraryProfile.Default, Title("9.99").RtProfile);
    }

    [Fact]
    public void Title_fields_override_the_profile()
    {
        MediusTitle title = Title(R2RtVersion);
        title.UniverseInfoLayout.ExtendedInfoLength = 128;
        title.Dme.ServerVersionLength = 16;
        ScertLayout layout = title.RtLayout;
        Assert.Equal(128, layout.UniverseExtendedInfoLength);
        Assert.False(layout.UniversePadAfterEndOfList);
        Assert.Equal(0, layout.JoinGameTrailingPadLength);
        Assert.Equal(16, layout.DmeServerVersionLength);
    }

    [Fact]
    public void Universe_info_under_303_has_256_byte_extended_info_and_ends_at_EndOfList()
    {
        ScertLayout layout = Title(R2RtVersion).RtLayout;
        byte[] bytes = AppBytes(new MediusUniverseVariableInformationResponse
        {
            MessageID = new MessageId("1"),
            InfoFilter = MediusUniverseVariableInformationInfoFilter.INFO_EXTRAINFO,
            ExtendedInfo = "x",
            EndOfList = true,
            ExtendedInfoLength = layout.UniverseExtendedInfoLength,
            PadAfterEndOfList = layout.UniversePadAfterEndOfList,
        });
        Assert.Equal((byte)NetMessageTypes.MessageClassLobbyExt, bytes[0]);
        Assert.Equal((byte)MediusLobbyExtMessageIds.UniverseVariableInformationResponse, bytes[1]);
        // MessageID[21] StatusCode InfoFilter ExtendedInfo[256] EndOfList (client serializer 0x817190).
        Assert.Equal(21 + 4 + 4 + 256 + 1, bytes.Length - 2);
        Assert.Equal((byte)'x', bytes[2 + 29]);
        Assert.Equal(1, bytes[^1]);
    }

    [Fact]
    public void Join_game_response_under_303_is_0xBC_bytes()
    {
        byte[] bytes = AppBytes(new MediusJoinGameResponse
        {
            MessageID = new MessageId("1"),
            StatusCode = MediusCallbackStatus.MediusSuccess,
            ConnectInfo = new NetConnectionInfo { Type = NetConnectionType.NetConnectionTypeClientServerTCPAuxUDP, WorldID = 7 },
            TrailingPadLength = Title(R2RtVersion).RtLayout.JoinGameTrailingPadLength,
        });
        Assert.Equal((byte)NetMessageTypes.MessageClassLobby, bytes[0]);
        Assert.Equal((byte)MediusLobbyMessageIds.JoinGameResponse, bytes[1]);
        // Swap 0x816580 / handler 0x80b1c0 return 0xbc.
        Assert.Equal(0xBC, bytes.Length - 2);
    }

    [Fact]
    public void Join_game_response_without_a_version_keeps_Horizons_4_trailing_bytes()
    {
        byte[] bytes = AppBytes(new MediusJoinGameResponse
        {
            MessageID = new MessageId("1"),
            ConnectInfo = new NetConnectionInfo(),
            TrailingPadLength = new MediusTitle().RtLayout.JoinGameTrailingPadLength,
        });
        Assert.Equal(0xBC + 4, bytes.Length - 2);
    }

    [Fact]
    public void Dme_server_version_under_303_carries_a_20_byte_string()
    {
        MediusTitle title = Title(R2RtVersion);
        byte[] bytes = AppBytes(new DMEServerVersion { Version = title.Dme.ServerVersion, VersionLength = title.RtLayout.DmeServerVersionLength });
        Assert.Equal((byte)NetMessageTypes.MessageClassDME, bytes[0]);
        Assert.Equal((byte)MediusDmeMessageIds.ServerVersion, bytes[1]);
        // Reader 0x851798; handler 0x8529b0 copies 0x14 bytes.
        Assert.Equal(20, bytes.Length - 2);
        Assert.Equal(Encoding.ASCII.GetBytes(title.Dme.ServerVersion.PadRight(20, '\0')), bytes[2..]);
    }

    [Theory]
    [InlineData("3.98", "3.98")]
    [InlineData(RfomRtVersion, "3.98")]
    [InlineData("3.02", "3.02")]
    [InlineData("3.02.200705211920", "3.02")] // Resistance: Fall of Man's in-game patch
    public void Version_line_selects_the_rfom_profiles(string rtVersion, string line)
    {
        Assert.True(ScertLibraryProfile.TryFind(rtVersion, out ScertLibraryProfile profile));
        Assert.Equal(line, profile.Version);
        // The disc (3.98) and its patch (3.02) lay out every profiled message the same way.
        Assert.Equal(new ScertLayout
        {
            UniverseExtendedInfoLength = 128,
            UniversePadAfterEndOfList = false,
            JoinGameTrailingPadLength = 0,
            DmeServerVersionLength = 16,
            SvoUrlFieldLength = 128,
            BinaryFwdOriginatorFirst = true,
            SupersetStatusCodeRepeated = true,
        }, Title(rtVersion).RtLayout);
    }

    [Fact]
    public void Universe_info_under_398_has_128_byte_extended_info_and_ends_at_EndOfList()
    {
        ScertLayout layout = Title(RfomRtVersion).RtLayout;
        byte[] bytes = AppBytes(new MediusUniverseVariableInformationResponse
        {
            MessageID = new MessageId("1"),
            InfoFilter = MediusUniverseVariableInformationInfoFilter.INFO_EXTRAINFO,
            ExtendedInfo = "x",
            EndOfList = true,
            ExtendedInfoLength = layout.UniverseExtendedInfoLength,
            PadAfterEndOfList = layout.UniversePadAfterEndOfList,
        });
        // MessageID[21] StatusCode InfoFilter ExtendedInfo[128] EndOfList (RFOM serializer 0x621d64).
        Assert.Equal(21 + 4 + 4 + 128 + 1, bytes.Length - 2);
        Assert.Equal(1, bytes[^1]);
    }

    [Fact]
    public void Join_game_response_and_dme_version_under_398()
    {
        ScertLayout layout = Title(RfomRtVersion).RtLayout;
        byte[] join = AppBytes(new MediusJoinGameResponse
        {
            MessageID = new MessageId("1"),
            ConnectInfo = new NetConnectionInfo(),
            TrailingPadLength = layout.JoinGameTrailingPadLength,
        });
        // RFOM handler 0x6166ac returns 0xbc.
        Assert.Equal(0xBC, join.Length - 2);

        MediusTitle title = Title(RfomRtVersion);
        byte[] version = AppBytes(new DMEServerVersion { Version = title.Dme.ServerVersion, VersionLength = layout.DmeServerVersionLength });
        // RFOM reader 0x65558c reads 0x10 bytes.
        Assert.Equal(16, version.Length - 2);
    }

    [Fact]
    public void Title_file_needs_only_the_version_line()
    {
        string root = Path.Combine(Path.GetTempPath(), "medius-rtversion-" + Guid.NewGuid().ToString("N"));
        string unknown = root + "-unknown";
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "medius"));
            File.WriteAllText(Path.Combine(root, MediusTitle.FileName), $$"""{ "AppIds": [ 1 ], "RtVersion": "{{R2RtVersion}}" }""");
            Directory.CreateDirectory(Path.Combine(unknown, "medius"));
            File.WriteAllText(Path.Combine(unknown, MediusTitle.FileName), """{ "AppIds": [ 2 ], "RtVersion": "9.99" }""");

            List<string> problems = [];
            List<MediusTitle> titles = MediusTitle.LoadAll(
            [
                new TitleDescriptor { TitleId = "rt303", Name = "RT 3.03", ProtocolFamily = "medius", ResourceDir = root },
                new TitleDescriptor { TitleId = "rt999", Name = "RT 9.99", ProtocolFamily = "medius", ResourceDir = unknown },
            ], problems);

            MediusTitle title = Assert.Single(titles);
            Assert.Same(ScertLibraryProfile.Rt303, title.RtProfile);
            Assert.Equal(ScertLibraryProfile.Rt303.Layout, title.RtLayout);
            Assert.Contains("9.99", Assert.Single(problems));
        }
        finally
        {
            foreach (string dir in new[] { root, unknown })
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
        }
    }
}
