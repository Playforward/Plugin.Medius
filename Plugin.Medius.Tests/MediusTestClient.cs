using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Cryptography;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using System.Net;
using System.Net.Sockets;

namespace Plugin.Medius.Tests;

/// <summary>
/// A scripted SCE-RT client: does what a PS2 or PS3 game's Medius library
/// does on connect, with its own RSA key and the server's public one, so the
/// server is exercised through real encryption.
/// </summary>
internal sealed class MediusTestClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private NetworkStream _stream = null!;
    private readonly List<byte> _pending = [];
    private readonly Queue<BaseScertMessage> _received = new();
    private readonly CipherService _cipher;
    private readonly ICipher _clientKey;
    private UdpClient? _udp;

    public MediusTestClient(int mediusVersion)
    {
        MediusVersion = mediusVersion;
        ICipherFactory factory = mediusVersion >= 112 ? new PS3CipherFactory() : new PS2CipherFactory();
        _cipher = new CipherService(factory);
        _clientKey = factory.CreateNew(CipherContext.RSA_AUTH);
    }

    public int MediusVersion { get; }

    public async Task ConnectAsync(int port)
    {
        await _tcp.ConnectAsync("127.0.0.1", port);
        _stream = _tcp.GetStream();
    }

    /// <summary>Hello, key exchange and RT connect, ending once the server says the connection is complete.</summary>
    public async Task HandshakeAsync(int appId, string? sessionKey = null, string? accessToken = null, uint worldId = 0)
    {
        await KeyExchangeAsync();

        Send(new RT_MSG_CLIENT_CONNECT_TCP
        {
            AppId = appId,
            TargetWorldId = worldId,
            Key = new RSA_KEY(),
            SessionKey = sessionKey,
            AccessToken = accessToken,
        });
        await ExpectAsync<RT_MSG_SERVER_CONNECT_REQUIRE>();
        Send(new RT_MSG_CLIENT_CONNECT_READY_REQUIRE());
        await ExpectAsync<RT_MSG_SERVER_CONNECT_ACCEPT_TCP>();
        Send(new RT_MSG_CLIENT_CONNECT_READY_TCP());
        await ExpectAsync<RT_MSG_SERVER_CONNECT_COMPLETE>();
    }

    /// <summary>
    /// The DME connect a game does after joining a game on the MLS: TCP with
    /// aux UDP, then the UDP connect, ending once the server says the
    /// connection is complete. Returns the server's accept, which carries
    /// the player's index in the world.
    /// </summary>
    public async Task<RT_MSG_SERVER_CONNECT_ACCEPT_TCP> DmeHandshakeAsync(int appId, NetConnectionInfo connectInfo)
    {
        await KeyExchangeAsync();

        Send(new RT_MSG_CLIENT_CONNECT_TCP_AUX_UDP
        {
            ARG1 = (uint)connectInfo.WorldID,
            AppId = appId,
            Key = new RSA_KEY(),
            SessionKey = connectInfo.SessionKey,
            AccessToken = connectInfo.AccessKey,
        });
        await ExpectAsync<RT_MSG_SERVER_CONNECT_REQUIRE>();
        Send(new RT_MSG_CLIENT_CONNECT_READY_REQUIRE());
        RT_MSG_SERVER_CONNECT_ACCEPT_TCP accept = await ExpectAsync<RT_MSG_SERVER_CONNECT_ACCEPT_TCP>();
        Send(new RT_MSG_CLIENT_CONNECT_READY_TCP
        {
            RecvFlag = RT_RECV_FLAG.RECV_BROADCAST | RT_RECV_FLAG.RECV_LIST | RT_RECV_FLAG.RECV_SINGLE | RT_RECV_FLAG.RECV_NOTIFICATION,
        });
        await ExpectAsync<RT_MSG_SERVER_STARTUP_INFO_NOTIFY>();
        RT_MSG_SERVER_INFO_AUX_UDP udpInfo = await ExpectAsync<RT_MSG_SERVER_INFO_AUX_UDP>();

        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _udp.Connect(new IPEndPoint(udpInfo.Ip, udpInfo.Port));
        SendUdp(new RT_MSG_CLIENT_CONNECT_AUX_UDP
        {
            WorldId = (uint)connectInfo.WorldID,
            ApplicationId = appId,
            EndPoint = (IPEndPoint)_udp.Client.LocalEndPoint!,
            PlayerId = accept.PlayerId,
            ScertId = (ushort)accept.ScertId,
        });
        RT_MSG_SERVER_CONNECT_ACCEPT_AUX_UDP udpAccept = await ExpectUdpAsync<RT_MSG_SERVER_CONNECT_ACCEPT_AUX_UDP>();
        if (udpAccept.PlayerId != accept.PlayerId)
            throw new InvalidOperationException($"UDP accepted player {udpAccept.PlayerId}, TCP {accept.PlayerId}");

        Send(new RT_MSG_CLIENT_CONNECT_READY_AUX_UDP());
        await ExpectAsync<RT_MSG_SERVER_CONNECT_COMPLETE>();
        await ExpectAppAsync<DMEServerVersion>();
        return accept;
    }

    private async Task KeyExchangeAsync()
    {
        Send(new RT_MSG_CLIENT_HELLO { Parameters = [2, (ushort)MediusVersion, 0x6d, 1, 1] });
        RT_MSG_SERVER_HELLO hello = await ExpectAsync<RT_MSG_SERVER_HELLO>();

        // The server's modulus sits at 0x194 of the PS3 hello's certificate, big-endian.
        byte[] certificate = hello.Certificate;
        byte[] modulus = certificate.AsSpan(0x194, 0x40).ToArray();
        _cipher.SetCipher(CipherContext.RSA_AUTH, new PS3_RSA(new Org.BouncyCastle.Math.BigInteger(1, modulus), Org.BouncyCastle.Math.BigInteger.ValueOf(17), Org.BouncyCastle.Math.BigInteger.ValueOf(17)));

        Send(new RT_MSG_CLIENT_CRYPTKEY_PUBLIC { Key = _clientKey.GetPublicKey().Reverse().ToArray() });
        _cipher.SetCipher(CipherContext.RSA_AUTH, _clientKey);
        RT_MSG_SERVER_CRYPTKEY_PEER peer = await ExpectAsync<RT_MSG_SERVER_CRYPTKEY_PEER>();
        _cipher.GenerateCipher(CipherContext.RC_CLIENT_SESSION, peer.Key);
    }

    public void Send(BaseScertMessage message)
    {
        foreach (byte[] frame in message.Serialize(MediusVersion, _cipher))
            _stream.Write(frame);
    }

    public void SendApp(BaseMediusMessage message) => Send(new RT_MSG_CLIENT_APP_TOSERVER { Message = message });

    /// <summary>Sends on the aux UDP channel the DME handshake opened; unencrypted, as games do.</summary>
    public void SendUdp(BaseScertMessage message)
    {
        foreach (byte[] frame in message.Serialize(MediusVersion, null!))
            _udp!.Send(frame);
    }

    /// <summary>Waits for a datagram and returns its first message, which must be a T.</summary>
    public async Task<T> ExpectUdpAsync<T>(int timeoutMs = 5000) where T : BaseScertMessage
    {
        using CancellationTokenSource timeout = new(timeoutMs);
        UdpReceiveResult datagram = await _udp!.ReceiveAsync(timeout.Token);
        BaseScertMessage? message = ScertDatagramPeer.Parse(datagram.Buffer, MediusVersion, _cipher).FirstOrDefault();
        return message as T ?? throw new InvalidOperationException($"expected {typeof(T).Name} over UDP, got {message}");
    }

    /// <summary>Waits for the next message of type T, skipping echoes.</summary>
    public async Task<T> ExpectAsync<T>(int timeoutMs = 5000) where T : BaseScertMessage
    {
        using CancellationTokenSource timeout = new(timeoutMs);
        while (true)
        {
            BaseScertMessage message = await NextAsync(timeout.Token);
            if (message is T wanted)
                return wanted;
            if (message is RT_MSG_SERVER_ECHO)
                continue;
            throw new InvalidOperationException($"expected {typeof(T).Name}, got {message}");
        }
    }

    public async Task<T> ExpectAppAsync<T>(int timeoutMs = 5000) where T : BaseMediusMessage
    {
        RT_MSG_SERVER_APP app = await ExpectAsync<RT_MSG_SERVER_APP>(timeoutMs);
        return app.Message as T ?? throw new InvalidOperationException($"expected {typeof(T).Name}, got {app.Message}");
    }

    private async Task<BaseScertMessage> NextAsync(CancellationToken token)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            if (_received.Count > 0)
                return _received.Dequeue();
            if (TryParse())
                continue;
            int read = await _stream.ReadAsync(buffer, token);
            if (read == 0)
                throw new EndOfStreamException("server closed the connection");
            _pending.AddRange(buffer.AsSpan(0, read).ToArray());
        }
    }

    private bool TryParse()
    {
        if (_pending.Count < 3)
            return false;
        byte id = _pending[0];
        int length = _pending[1] | (_pending[2] << 8);
        bool encrypted = id >= 0x80 && length > 0;
        int header = encrypted ? 7 : 3;
        if (_pending.Count < header + length)
            return false;
        byte[]? hash = encrypted ? _pending.GetRange(3, 4).ToArray() : null;
        byte[] payload = _pending.GetRange(header, length).ToArray();
        _pending.RemoveRange(0, header + length);
        _received.Enqueue(BaseScertMessage.Instantiate((RT_MSG_TYPE)(id & 0x7F), hash, payload, MediusVersion, _cipher));
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _udp?.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}
