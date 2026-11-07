using Plugin.Medius.Extensions.Resistance2;
using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Cryptography;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using System.Net;

namespace Plugin.Medius.Tests;

/// <summary>
/// The R2 host seat replayed against PSRewired's: a solo custom-match lobby as
/// the client played it (MediusProxy, decrypted, game at 00:29:39), the host's
/// answers byte for byte [live].
/// </summary>
public sealed class R2MatchHostTests
{
    /// <summary>Collects what a ScertConnection sends.</summary>
    private sealed class Capture : IScertTransport
    {
        public List<byte[]> Frames { get; } = [];
        public Guid Id { get; } = Guid.NewGuid();
        public IPEndPoint? Remote => new(IPAddress.Loopback, 6092);
        public int LocalPort => 11001;
        public bool RtEncryption => false;
        public void Send(IReadOnlyList<byte[]> frames) => Frames.AddRange(frames);
        public void Disconnect() { }
    }

    private static (R2MatchHost Host, DmePlayer Player, Capture Sent) Lobby()
    {
        MediusTitle title = new() { GameServer = new MediusGameServerSettings { Port = 11001, HostSeat = true } };
        MediusClient client = new(title, 21734, 112);
        MediusGame game = new(1, title, new MediusMatchPool { MaxPlayers = 8 }, client);
        DmePlayer player = game.Reserve(client)!;
        Capture sent = new();
        ScertConnection connection = new(sent, "test", new RsaKeyPair(), false);
        connection.SetMediusVersion(112);
        player.Connection = connection;
        player.Joined = true;
        return (new R2MatchHost(game, new Resistance2HostSettings()), player, sent);
    }

    /// <summary>The APP_SINGLE payloads from index 0, as hex.</summary>
    private static List<string> FromHost(Capture sent)
    {
        List<string> payloads = [];
        foreach (byte[] frame in sent.Frames)
        {
            var single = Assert.IsType<RT_MSG_CLIENT_APP_SINGLE>(ScertDatagramPeer.Parse(frame, 112, null).Single());
            Assert.Equal(0, single.TargetOrSource);
            payloads.Add(Convert.ToHexString(single.Payload));
        }
        sent.Frames.Clear();
        return payloads;
    }

    private static byte[] H(string hex) => Convert.FromHexString(hex);

    [Fact]
    public void Solo_lobby_is_answered_as_psrewired_did()
    {
        (R2MatchHost host, DmePlayer player, Capture sent) = Lobby();
        Assert.Equal(1, player.Index);

        // 00:29:39.487 key hello, state 2, step 3 (list)
        host.Received(player, H("00100001000000000000000000000BD708E80B54DA0F030293496057E3FF9A26E604E0E7D3BC7EE8D00D0FE8B204D20B9E2B39261F9246620C7C3531A8D484AC4C7788212BB04BD64DEAF251C5CC"), direct: true);
        host.Received(player, H("00160201000000000000000000008F84083A"), direct: true);
        host.Received(player, H("0015030100000000"), direct: false);
        Assert.Equal([
            "0010" + new string('0', 76 * 2),
            "001605000000000000000100000000000000",
            "000900000000",
            "001701000000",
        ], FromHost(sent));

        // 00:29:39.840 info (broadcast) and join announce (list)
        host.Received(player, H("000F01004000000000000000000000000000000000F3554E4B4E4F574E0000000000"), direct: false);
        host.Received(player, H("02B000000000000755CA000175650000000001"), direct: false);
        Assert.Equal(["000F0000400000000000000000000000000000000000523220484F53540000000000", "02B100"], FromHost(sent));

        // 00:29:40.016 info to the host, in the lobby
        host.Received(player, H("000F01004000000000000000000070F800D000000000554E4B4E4F574E0000000000"), direct: true);
        host.Received(player, H("02AD00090000240000008A"), direct: true);
        host.Received(player, H("02AD0005008A23"), direct: true);
        host.Received(player, H("02AD0005002121"), direct: true);
        Assert.Equal(["02AC0000000501", "02AE000900010000000021"], FromHost(sent));

        // 00:29:41.989 RT 0x39: the game runs
        host.ReceivedRt(player, new RawScertMessage((RT.Common.RT_MSG_TYPE)0x39) { Contents = H("0400000001") });
        Assert.Equal(["001604000000000000000100000000000000", "00160501000000000000020000008F84083A"], FromHost(sent));

        // 00:29:43 echo, status, variables
        host.Received(player, H("02AD000C108000000000000027C4"), direct: true);
        host.Received(player, H("02AE000E4084000000010004000027C4"), direct: true);
        host.Received(player, H("02AC000A0001000020000101"), direct: true);
        host.Received(player, H("02AD0005106221"), direct: true);
        Assert.Equal(["02AD000C108000000000000027C4", "02AE000E0084000000010004000027C4", "02AC0001000CFF00010000200001"], FromHost(sent));
    }

    /// <summary>
    /// After its profile the player gets the match objects, then writes its record, two
    /// fields and the match settings; the host answers each write with the update
    /// (Data/r2-host-profile.txt, custom match at 23:48:07) [live].
    /// </summary>
    [Fact]
    public void Profile_and_object_writes_are_answered_as_psrewired_did()
    {
        (R2MatchHost host, DmePlayer player, Capture sent) = Lobby();
        List<string> expected = [];
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "r2-host-profile.txt")))
        {
            if (line.StartsWith('#'))
                continue;
            string[] fields = line.Split(' ');
            if (fields[0] == "C->H")
                host.Received(player, H(fields[1]), direct: true);
            else
                expected.Add(fields[1]);
        }
        Assert.Equal(7, expected.Count);
        Assert.Equal(expected, FromHost(sent));
    }

    /// <summary>
    /// Two players: the creator's lobby, then a second player joining it, the host's
    /// answers to both byte for byte and in order (Data/r2-host-join.txt, PSRewired,
    /// 2026-10-30 01:25) [live].
    /// </summary>
    [Fact]
    public void A_second_player_joins_as_on_psrewired()
    {
        (R2MatchHost host, DmePlayer first, Capture firstSent) = Lobby();
        DmePlayer second = first.Game.Reserve(new MediusClient(first.Game.Title, 21734, 112))!;
        Capture secondSent = new();
        ScertConnection connection = new(secondSent, "test", new RsaKeyPair(), false);
        connection.SetMediusVersion(112);
        second.Connection = connection;
        Assert.Equal(2, second.Index);

        Dictionary<string, (DmePlayer Player, Capture Sent, List<string> Expected)> players = new()
        {
            ["1"] = (first, firstSent, []),
            ["2"] = (second, secondSent, []),
        };
        foreach (string line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Data", "r2-host-join.txt")))
        {
            if (line.StartsWith('#'))
                continue;
            string[] f = line.Split(' ');
            (DmePlayer player, _, List<string> expected) = players[f[1]];
            if (f[1] == "2")
                second.Joined = true;
            switch (f[2])
            {
                case "OUT": expected.Add(f[3]); break;
                case "RT39": host.ReceivedRt(player, new RawScertMessage((RT.Common.RT_MSG_TYPE)0x39) { Contents = H(f[3]) }); break;
                default: host.Received(player, H(f[3]), direct: f[2] == "H"); break;
            }
        }
        Assert.Equal(players["1"].Expected, FromHost(firstSent));
        Assert.Equal(players["2"].Expected, FromHost(secondSent));
    }

    /// <summary>The leaving player's object goes, and its session leaves the match object [live 01:29:32].</summary>
    [Fact]
    public void A_leaving_player_is_deleted_and_leaves_the_match_object()
    {
        (R2MatchHost host, DmePlayer first, Capture firstSent) = Lobby();
        DmePlayer second = first.Game.Reserve(new MediusClient(first.Game.Title, 21734, 112))!;
        ScertConnection connection = new(new Capture(), "test", new RsaKeyPair(), false);
        connection.SetMediusVersion(112);
        second.Connection = connection;
        second.Joined = true;
        host.Received(first, H("02AD002C10400001010000D5574C01007F000000B480000175655DA3715D00000000D00109000000000000DFDE58"), direct: true);
        host.Received(second, H("02AD002C1040000101FF00D5574C02007F000000B480000175A20A6945E200000000D00109000000000000DFDE58"), direct: true);
        FromHost(firstSent);

        host.PlayerLeaving(second);
        Assert.Equal(["02AF0002", "02AC00010033FF008200010001000000005DA3715D0000000000000000000000000000000000000000000000000001010000000000"], FromHost(firstSent));
    }

    [Theory]
    [InlineData("00100001000000000000000000000BD708E80B54DA0F030293496057E3FF9A26E604E0E7D3BC7EE8D00D0FE8B204D20B9E2B39261F9246620C7C3531A8D484AC4C7788212BB04BD64DEAF251C5CC")]
    [InlineData("00160201000000000000000000008F84083A")]
    [InlineData("0015030100000000")]
    [InlineData("000F01004000000000000000000070F800D000000000554E4B4E4F574E0000000000")]
    [InlineData("02B000000000000755CA000175650000000001")]
    [InlineData("02AC000A0001000020000101")]
    [InlineData("02AC000C00010000120001000100")]
    [InlineData("02AD0008464000100005")]
    public void Messages_read_and_write_back_whole(string hex)
    {
        R2NetMessage message = R2NetMessage.Parse(H(hex));
        Assert.IsNotType<R2RawMessage>(message);
        Assert.Equal(hex, Convert.ToHexString(message.ToBytes()));
    }
}
