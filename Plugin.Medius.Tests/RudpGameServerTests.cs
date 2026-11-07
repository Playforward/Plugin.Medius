using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using Player = Plugin.Medius.Tests.GameFlowTests.Player;

namespace Plugin.Medius.Tests;

/// <summary>
/// The game server over RUdp, end to end: players join a game at the MLS and
/// connect to the RUDP listener with the access key they got, as Resistance 2
/// does with PSRewired's server [live MediusProxy 2026-10-29].
/// </summary>
[Collection(MediusServerCollection.Name)]
public sealed class RudpGameServerTests(LobbyFlowFixture fixture)
{
    private const RT_RECV_FLAG AllFlags = RT_RECV_FLAG.RECV_BROADCAST | RT_RECV_FLAG.RECV_LIST | RT_RECV_FLAG.RECV_SINGLE | RT_RECV_FLAG.RECV_NOTIFICATION;

    private async Task<NetConnectionInfo> JoinAsync(Player player, int worldId)
    {
        MediusJoinGameResponse join = await player.JoinGameAsync(worldId);
        Assert.Equal(MediusCallbackStatus.MediusSuccess, join.StatusCode);
        return join.ConnectInfo;
    }

    /// <summary>The RT connect over RUdp: hello, connect -> version + accept, ready -> complete + startup info.</summary>
    private static async Task<RT_MSG_SERVER_CONNECT_ACCEPT_TCP> ConnectAsync(RudpTestClient client, NetConnectionInfo info)
    {
        await client.HandshakeAsync();
        client.Send(new RT_MSG_CLIENT_HELLO { Parameters = [1, (ushort)client.MediusVersion, 0] });
        RT_MSG_SERVER_HELLO hello = await client.ExpectAsync<RT_MSG_SERVER_HELLO>();
        Assert.Equal(client.MediusVersion, hello.ARG1);
        client.Send(new RT_MSG_CLIENT_CONNECT_TCP
        {
            TargetWorldId = (uint)info.WorldID,
            AppId = LobbyFlowFixture.AppId,
            Key = new RSA_KEY(),
            SessionKey = info.SessionKey,
            AccessToken = info.AccessKey,
        });
        await client.ExpectAppAsync<DMEServerVersion>();
        RT_MSG_SERVER_CONNECT_ACCEPT_TCP accept = await client.ExpectAsync<RT_MSG_SERVER_CONNECT_ACCEPT_TCP>();
        client.Send(new RT_MSG_CLIENT_CONNECT_READY_TCP { RecvFlag = AllFlags });
        await client.ExpectAsync<RT_MSG_SERVER_CONNECT_COMPLETE>();
        await client.ExpectAsync<RT_MSG_SERVER_STARTUP_INFO_NOTIFY>();
        return accept;
    }

    [Fact]
    public async Task Two_players_connect_and_relay_over_rudp()
    {
        await using Player alice = await Player.LoginAsync(fixture, "alice-rudp");
        await using Player bob = await Player.LoginAsync(fixture, "bob-rudp");
        int worldId = await alice.CreateGameAsync("Rudp Test", maxPlayers: 4);
        NetConnectionInfo aliceInfo = await JoinAsync(alice, worldId);
        NetConnectionInfo bobInfo = await JoinAsync(bob, worldId);

        await using RudpTestClient aliceGame = new(fixture.RudpPort, aliceInfo.AccessKey, helloFlags: 0x89);
        RT_MSG_SERVER_CONNECT_ACCEPT_TCP aliceAccept = await ConnectAsync(aliceGame, aliceInfo);

        await using RudpTestClient bobGame = new(fixture.RudpPort, bobInfo.AccessKey, connectionId: 0x8C, helloFlags: 0x88);
        RT_MSG_SERVER_CONNECT_ACCEPT_TCP bobAccept = await ConnectAsync(bobGame, bobInfo);
        Assert.NotEqual(aliceAccept.PlayerId, bobAccept.PlayerId);
        Assert.Equal(aliceAccept.PlayerId, (int)(await bobGame.ExpectAsync<RT_MSG_SERVER_CONNECT_NOTIFY>()).PlayerIndex);
        Assert.Equal(bobAccept.PlayerId, (int)(await aliceGame.ExpectAsync<RT_MSG_SERVER_CONNECT_NOTIFY>()).PlayerIndex);

        // R2 broadcasts unreliably; the relay arrives reliably, from bob.
        bobGame.Send(new RT_MSG_CLIENT_APP_BROADCAST { Payload = [1, 2, 3] }, reliable: false);
        RT_MSG_CLIENT_APP_SINGLE relayed = await aliceGame.ExpectAsync<RT_MSG_CLIENT_APP_SINGLE>();
        Assert.Equal(bobAccept.PlayerId, (int)relayed.TargetOrSource);
        Assert.Equal(new byte[] { 1, 2, 3 }, relayed.Payload);

        aliceGame.SendApp(new DMETypePing { RequestEcho = 1 }, reliable: false);
        DMETypePing pong = await aliceGame.ExpectAppAsync<DMETypePing>();
        Assert.InRange(pong.Time, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 5);

        aliceGame.Send(new RT_MSG_CLIENT_PEER_QUERY { PlayerIndex = bobAccept.PlayerId });
        RT_MSG_SERVER_PEER_QUERY_NOTIFY peer = await aliceGame.ExpectAsync<RT_MSG_SERVER_PEER_QUERY_NOTIFY>();
        Assert.Equal(bobAccept.PlayerId, (int)peer.PlayerIndex);
        Assert.True(peer.MinLatency <= peer.AverageLatency && peer.AverageLatency <= peer.MaxLatency);

        aliceGame.Send(new RT_MSG_CLIENT_TOKEN_MESSAGE { Contents = [0x02, 0x42, 0x11] });
        Assert.Equal(new byte[] { 0x08, 0x42, 0x11 }, (await aliceGame.ExpectAsync<RT_MSG_SERVER_TOKEN_MESSAGE>()).Contents);

        // Leaving is confirmed, and the others are told.
        bobGame.Send(new RT_MSG_CLIENT_DISCONNECT_WITH_REASON { Reason = 1 });
        await bobGame.ExpectAsync<RT_MSG_CLIENT_DISCONNECT>();
        Assert.Equal(bobAccept.PlayerId, (int)(await aliceGame.ExpectAsync<RT_MSG_SERVER_DISCONNECT_NOTIFY>()).PlayerIndex);
    }

    [Theory]
    [InlineData(0x89, 0x41)] // our test 2026-10-29: answering 0x8B left the client retrying [live]
    [InlineData(0x88, 0x40)] // PSRewired, first session [live]
    [InlineData(0x8B, 0x43)] // PSRewired [live]
    public async Task Hello_flags_are_echoed(byte helloFlags, byte ackFlags)
    {
        await using Player player = await Player.LoginAsync(fixture, $"rudp-flags-{helloFlags:X2}");
        int worldId = await player.CreateGameAsync($"Flags {helloFlags:X2}", maxPlayers: 2);
        NetConnectionInfo info = await JoinAsync(player, worldId);

        await using RudpTestClient game = new(fixture.RudpPort, info.AccessKey, helloFlags: helloFlags);
        RudpPacket hello = await game.HelloAsync();
        Assert.Equal(helloFlags, hello.Flags);
        Assert.Equal(game.Hello.Id, game.ServerHello!.Id);
        Assert.NotEqual(game.Hello.Nonce, game.ServerHello.Nonce);
        using CancellationTokenSource timeout = new(5000);
        while (!game.Datagrams.Any(p => p.Messages.Count == 0 && p.Acks.Count > 0))
            await game.ReceiveAsync(timeout.Token);
        Assert.Equal(ackFlags, game.Datagrams.First(p => p.Messages.Count == 0 && p.Acks.Count > 0).Flags);
    }

    [Fact]
    public async Task A_new_connection_from_the_same_port_replaces_the_old()
    {
        await using Player player = await Player.LoginAsync(fixture, "rudp-reuse");
        int first = await player.CreateGameAsync("Reuse 1", maxPlayers: 2);
        NetConnectionInfo firstInfo = await JoinAsync(player, first);
        int port;
        await using (RudpTestClient game = new(fixture.RudpPort, firstInfo.AccessKey, connectionId: 0x83))
        {
            await ConnectAsync(game, firstInfo);
            port = game.LocalPort;
        }

        // The game reuses its port for the next game, with a new connection byte and id [live 14:09].
        int second = await player.CreateGameAsync("Reuse 2", maxPlayers: 2);
        NetConnectionInfo secondInfo = await JoinAsync(player, second);
        await using RudpTestClient again = new(fixture.RudpPort, secondInfo.AccessKey, connectionId: 0x11, localPort: port);
        RudpPacket hello = await again.HelloAsync();
        Assert.Equal(0x11, hello.ConnectionId);
        Assert.Equal(again.Hello.Id, again.ServerHello!.Id);
        await ConnectAsync(again, secondInfo);
    }

    [Fact]
    public async Task A_hello_for_no_issued_access_key_is_not_answered()
    {
        await using RudpTestClient stranger = new(fixture.RudpPort, "0123456789ABCDEF");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using CancellationTokenSource timeout = new(1000);
            stranger.SendPacket(new RudpPacket
            {
                ConnectionId = 0x41,
                Flags = 0x8B,
                Messages = [new RudpMessage { HeaderFlags = 0x5000, Sequence = 1, Payload = stranger.Hello.ToBytes() }],
            });
            await stranger.ReceiveAsync(timeout.Token);
        });
    }
}
