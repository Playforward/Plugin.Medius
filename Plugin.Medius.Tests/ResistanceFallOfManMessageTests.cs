using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;

namespace Plugin.Medius.Tests;

/// <summary>
/// Resistance: Fall of Man's messages as captured from PSRewired's server through MediusProxy
/// (2026-11-01, plaintext class + type + body; ServerApp-Titles/ResistanceFallOfMan/medius/PROTOCOL.md).
/// Each one is read with its model and must write back to the same bytes, which checks the layouts
/// taken from the client's codecs.
/// </summary>
public sealed class ResistanceFallOfManMessageTests
{
    private const int Ps3 = 113;

    private static byte[] Write(BaseMediusMessage message, bool fromClient)
    {
        using MemoryStream stream = new();
        using (MessageWriter writer = new(stream) { MediusVersion = Ps3 })
        {
            if (fromClient)
                new RT_MSG_CLIENT_APP_TOSERVER { Message = message }.Serialize(writer);
            else
                new RT_MSG_SERVER_APP { Message = message }.Serialize(writer);
        }
        return stream.ToArray();
    }

    private static T Read<T>(string hex, bool fromClient) where T : BaseMediusMessage
    {
        using MessageReader reader = new(new MemoryStream(Convert.FromHexString(hex))) { MediusVersion = Ps3 };
        return Assert.IsType<T>(BaseMediusMessage.Instantiate(reader, fromClient));
    }

    private static T RoundTrip<T>(string hex, bool fromClient) where T : BaseMediusMessage
    {
        T message = Read<T>(hex, fromClient);
        Assert.Equal(hex, Convert.ToHexString(Write(message, fromClient)));
        return message;
    }

    [Fact]
    public void Extended_session_begin_has_a_pad_and_connection_class()
    {
        var m = RoundTrip<MediusExtendedSessionBeginRequest>("0427320000000000000000000000000000000000000000000000010000000300000062000000340A2245", true);
        Assert.Equal(MediusConnectionType.Ethernet, m.ConnectionClass);
        Assert.Equal(3, m.ClientVersionMajor);
        Assert.Equal(98, m.ClientVersionMinor);
    }

    [Fact]
    public void Localization_requests_of_both_builds()
    {
        var disc = RoundTrip<MediusSetLocalizationParamsRequest2>("045A33000000000000000000000000000000000000000037313165613333323261623066383961000000020000000100000000000000", true);
        Assert.Equal(MediusLanguageType.MediusLanguage_USEnglish, disc.Language);
        var patch = RoundTrip<MediusSetLocalizationParamsRequest1>("047B3300000000000000000000000000000000000000006336353866343437313031346131303700000002000000010000000000000001000000", true);
        Assert.Equal(1u, patch.Unk34);
    }

    [Fact]
    public void Buddy_list_and_clan_members()
    {
        RoundTrip<MediusGetBuddyListRequest>("013D3134000000000000000000000000000000000000006336353866343437313031346131303700", true);
        var buddies = RoundTrip<MediusGetBuddyListResponse>("013E313400000000000000000000000000000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000000000001000000", false);
        Assert.Equal(MediusCallbackStatus.MediusNoResult, buddies.StatusCode);
        Assert.True(buddies.EndOfList);

        var request = RoundTrip<MediusGetClanMemberListRequest>("01903232000000000000000000000000000000000000006336353866343437313031346131303700000010370000", true);
        Assert.Equal(14096, request.ClanID);
        var member = RoundTrip<MediusGetClanMemberListResponse>("01913232000000000000000000000000000000000000000000003076010064796C6C616E35303100000000000000000000000000000000000000000000000000000001000000", false);
        Assert.Equal(95792, member.AccountID);
        Assert.Equal("dyllan501", member.AccountName);
        Assert.True(member.EndOfList);
    }

    [Fact]
    public void Supersets_repeat_the_status_code_after_EndOfList()
    {
        const string hex = "045E31320000000000000000000000000000000000000000000000000000000001000000000000000A000000310000000400000044454154484D4154434843757272656E746C79206E6F742066756E6374696F6E616C2E20456E74657220617420796F7572206F776E207269736B2E00000100";
        byte[] bytes = Convert.FromHexString(hex);
        MediusMatchListSupersetsResponse m = new() { StatusCodeRepeated = ScertLibraryProfile.Rt302.Layout.SupersetStatusCodeRepeated };
        using (MessageReader reader = new(new MemoryStream(bytes, 2, bytes.Length - 2)) { MediusVersion = Ps3 })
            m.Deserialize(reader);
        Assert.Equal(1u, m.SupersetID);
        Assert.Equal("DEATHMATCH", m.SupersetName);
        Assert.Equal("\0\0\u0001\0", m.SupersetExtraInfo);
        Assert.Equal(hex, Convert.ToHexString(Write(m, fromClient: false)));
    }

    [Fact]
    public void Party_join_carries_the_key_and_answers_a_join_game_response()
    {
        var request = RoundTrip<MediusJoinGameExRequest>("0467343100000000000000000000000000000000000000633635386634343731303134613130370000002A040000000000000000000000000000000000000000000000000000000000000000000004000000334C37D4390DE5058B07B60CCE3043ACCC08686BAAC12EF51A6C7669EE501379873F34AD6C4498762142E5898F620C754D9EB75F0F7BA979C54A46A15AAF73AE000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000", true);
        Assert.Equal(1066, request.MediusWorldID);
        Assert.Equal(MediusGameHostType.MediusGameHostClientServerAuxUDP, request.GameHostType);
        var response = RoundTrip<MediusJoinGameExResponse>("04683431000000000000000000000000000000000000000000000000000004000000030000000100000036372E3232322E3135362E3235300000F92A00000300000036372E3232322E3135362E3235300000562700002A04000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000633635386634343731303134613130370035666534653137316430333161313330000000", false);
        Assert.Equal(NetConnectionType.NetConnectionTypeClientServerTCPAuxUDP, response.ConnectInfo.Type);
        Assert.Equal(11001u, response.ConnectInfo.AddressList.AddressList[0].Port);
        Assert.Equal(1066, response.ConnectInfo.WorldID);
    }

    [Fact]
    public void Quick_match_request_and_referral()
    {
        var request = RoundTrip<MediusMatchFindGameRequest>("0478333300000000000000000000000000000000000000613930366430643465633334653131630001000000010000000000000000000000000000000000000030760100", true);
        Assert.Equal(1u, request.SupersetID);
        Assert.Equal([95792], request.AccountIDs);
        var referral = RoundTrip<MediusMatchFindGameResponse>("046033330000000000000000000000000000000000000008000000010000000100000001000000010000003139322E3136382E322E3130330000005F2700000000000000000000000000000000000000000000000000000100000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000613930366430643465633334653131630062633238353038343161323837613300000000", false);
        Assert.Equal(MediusCallbackStatus.MediusMatchTypeReferral, referral.StatusCode);
        Assert.Equal("192.168.2.103", referral.ConnectInfo.AddressList.AddressList[0].Address);
        Assert.Equal(10079u, referral.ConnectInfo.AddressList.AddressList[0].Port);
    }

    [Fact]
    public void Text_filter_reads_and_echoes_the_text()
    {
        var request = RoundTrip<MediusTextFilterRequest1>("04753139000000000000000000000000000000000000006336353866343437313031346131303700010000000400000074657374", true);
        Assert.Equal("test", request.Text);

        // PSRewired pads its answer to 513 bytes; the client reads TextLength bytes (codec 0x65850c).
        string psrewired = "0476313900000000000000000000000000000000000000000000000500000074657374" + new string('0', 2 * 480);
        var response = Read<MediusTextFilterResponse1>(psrewired, false);
        Assert.Equal(MediusCallbackStatus.MediusSuccess, response.StatusCode);
        Assert.Equal("test", response.Text);
        Assert.Equal(psrewired[..(2 * 36)], Convert.ToHexString(Write(response, fromClient: false)));
    }
}
