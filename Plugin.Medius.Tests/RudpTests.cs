using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;
using Plugin.Medius.Transport;
using System.Net;
using System.Text;

namespace Plugin.Medius.Tests;

/// <summary>
/// The RUdp session key and the RT messages carried over RUdp, checked against
/// captured sessions: two of Resistance 2 with our server (2026-10-29 09:10 and
/// 14:05) and one with PSRewired's (MediusProxy, every one of its 14532
/// encrypted messages decrypts with these keys).
/// </summary>
public sealed class RudpCryptoTests
{
    private static byte[] H(string hex) => Convert.FromHexString(hex);

    [Theory]
    [InlineData("f4c676fef2debb50", "3ACF12A2")] // PSRewired
    [InlineData("5CB64255F19191E6", "9DFEAA9D")] // ours, 09:10
    [InlineData("299DE89BEC98F8E1", "5C1309D4")] // ours, 14:05
    [InlineData("C5916F2B9791AF59", "2212D880")] // ours, 14:09
    public void Hello_id_is_the_hash_of_the_access_key(string accessKey, string id) =>
        Assert.Equal(id, Convert.ToHexString(RudpCrypto.HelloId(accessKey)));

    [Theory]
    // PSRewired: client and server confirmations
    [InlineData("f4c676fef2debb50", "8AB36A40776194181E6D9C595AACF715", "12E7EF40A17DA72F05C5A6ECDC1AEE9F", "F95B199A3F6909B4AEBE819201E8F5FC", "D0E65666", "12E7EF40A17DA72F05C5A6ECDC1AEE9F")]
    [InlineData("f4c676fef2debb50", "8AB36A40776194181E6D9C595AACF715", "12E7EF40A17DA72F05C5A6ECDC1AEE9F", "ABBD2140BEF738571773149B9514AAB9", "F8086D7D", "8AB36A40776194181E6D9C595AACF715")]
    // ours: the client's confirmation that went unanswered
    [InlineData("5CB64255F19191E6", "92ABA4BC02370BE2CF2F97A464EDE297", "CA968C3459958127224BF64CD1D6C86E", "E3FD3FBEB19121EB847D148E1547F77A", "3F0B5172", "CA968C3459958127224BF64CD1D6C86E")]
    [InlineData("299DE89BEC98F8E1", "B55E7691F949B778519945D40A882BD6", "D7235A10A04F958635EC073AC186A5B7", "3DEC938581C894A374840B6F464AB878", "A079F47B", "D7235A10A04F958635EC073AC186A5B7")]
    public void Confirmations_decrypt_to_the_senders_nonce(string accessKey, string serverNonce, string clientNonce, string body, string hash, string plain)
    {
        var cipher = RudpCrypto.Cipher(RudpCrypto.SessionKey(H(serverNonce), H(clientNonce), accessKey));
        Assert.True(cipher.Decrypt(H(body), H(hash), out byte[] decrypted));
        Assert.Equal(plain, Convert.ToHexString(decrypted));
    }

    [Fact]
    public void Our_encryption_is_what_the_client_checks()
    {
        byte[] key = RudpCrypto.SessionKey(H("8AB36A40776194181E6D9C595AACF715"), H("12E7EF40A17DA72F05C5A6ECDC1AEE9F"), "f4c676fef2debb50");
        RudpCrypto.Cipher(key).Encrypt(H("8AB36A40776194181E6D9C595AACF715"), out byte[] encrypted, out byte[] hash);
        Assert.Equal("ABBD2140BEF738571773149B9514AAB9", Convert.ToHexString(encrypted));
        Assert.Equal("F8086D7D", Convert.ToHexString(hash));
    }
}

/// <summary>RT messages as PSRewired sends them over RUdp to Resistance 2 (version 112) [live].</summary>
public sealed class RudpRtMessageTests
{
    private const int Version = 112;

    private static string Payload(BaseScertMessage message) =>
        Convert.ToHexString(message.Serialize(Version, null!).Single()[3..]);

    private static T Read<T>(RT_MSG_TYPE id, string payload) where T : BaseScertMessage =>
        Assert.IsType<T>(BaseScertMessage.Instantiate(id, null!, Convert.FromHexString(payload), Version, null!));

    [Fact]
    public void Server_hello_is_the_version() =>
        Assert.Equal("70000000", Payload(new RT_MSG_SERVER_HELLO { ArgsOnly = true, ARG1 = Version, ARG2 = 0 }));

    [Fact]
    public void Connect_carries_world_app_and_access_key()
    {
        RT_MSG_CLIENT_CONNECT_TCP connect = Read<RT_MSG_CLIENT_CONNECT_TCP>(RT_MSG_TYPE.RT_MSG_CLIENT_CONNECT_TCP,
            "C50F0000E654" + new string('0', 4 + 128) + "38303637633833373936303765366631006634633637366665663264656262353000");
        Assert.Equal(4037u, connect.TargetWorldId);
        Assert.Equal(21734, connect.AppId);
        Assert.Equal("f4c676fef2debb50", connect.AccessToken);
    }

    [Fact]
    public void Accept_names_index_scert_id_count_and_the_clients_address() =>
        Assert.Equal("01000200000002003137332E37332E3134302E3534000000", Payload(new RT_MSG_SERVER_CONNECT_ACCEPT_TCP
        {
            PlayerId = 1,
            ScertId = 2,
            PlayerCount = 2,
            IP = IPAddress.Parse("173.73.140.54"),
        }));

    [Fact]
    public void Startup_info_and_host_seat_notify()
    {
        Assert.Equal("044B060000", Payload(new RT_MSG_SERVER_STARTUP_INFO_NOTIFY { GameHostType = 4, Timestamp = 0x064B }));
        // index 0, ScertId 1, 0, then the address as text; 86 bytes in all [live]
        string notify = "000001000000" + Convert.ToHexString(Encoding.ASCII.GetBytes("67.222.156.250"));
        Assert.Equal(notify.PadRight(86 * 2, '0'),
            Payload(new RT_MSG_SERVER_CONNECT_NOTIFY { PlayerIndex = 0, ScertId = 1, IP = IPAddress.Parse("67.222.156.250") }));
    }

    [Fact]
    public void Peer_query_and_its_answer()
    {
        RT_MSG_CLIENT_PEER_QUERY query = Read<RT_MSG_CLIENT_PEER_QUERY>(RT_MSG_TYPE.RT_MSG_CLIENT_PEER_QUERY, "010500");
        Assert.Equal(1, query.QueryType);
        Assert.Equal(5, query.PlayerIndex);
        Assert.Equal("0100005D004F005300", Payload(new RT_MSG_SERVER_PEER_QUERY_NOTIFY { QueryType = 1, PlayerIndex = 0, MaxLatency = 93, MinLatency = 79, AverageLatency = 83 }));
    }

    [Fact]
    public void Dme_ping_and_its_answer()
    {
        RT_MSG_CLIENT_APP_TOSERVER ping = Read<RT_MSG_CLIENT_APP_TOSERVER>(RT_MSG_TYPE.RT_MSG_CLIENT_APP_TOSERVER, "00010000000000010000");
        DMETypePing request = Assert.IsType<DMETypePing>(ping.Message);
        Assert.Equal(1, request.RequestEcho);
        Assert.Equal("0001B4CBE26A00000000", Payload(new RT_MSG_SERVER_APP { Message = new DMETypePing { Time = 0x6AE2CBB4 } }));
    }
}
