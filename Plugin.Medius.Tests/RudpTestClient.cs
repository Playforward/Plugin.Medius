using Plugin.Medius.RT.Cryptography;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Plugin.Medius.Tests;

/// <summary>
/// A scripted SCE-RT RUdp client: does what Resistance 2 does on a type-5 game
/// server (hello, key confirmation, 01 / 00000000, then RT frames in encrypted
/// reliable messages), with the session key derived from the access key the
/// lobby handed out, as the game derives it.
/// </summary>
internal sealed class RudpTestClient : IAsyncDisposable
{
    private readonly UdpClient _udp;
    private readonly string _accessKey;
    private readonly Queue<BaseScertMessage> _received = new();
    private readonly HashSet<ushort> _delivered = [];
    private readonly List<byte> _pending = [];
    private ushort _sequence = (ushort)RandomNumberGenerator.GetInt32(0x10000);
    private PS3_RCQ? _cipher;

    public RudpTestClient(int port, string accessKey, byte connectionId = 0x41, byte helloFlags = 0x8B, int localPort = 0)
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, localPort));
        _udp.Connect(IPAddress.Loopback, port);
        _accessKey = accessKey;
        ConnectionId = connectionId;
        HelloFlags = helloFlags;
        Hello = new RudpHello { Id = RudpCrypto.HelloId(accessKey), Nonce = RandomNumberGenerator.GetBytes(16) };
    }

    public int MediusVersion { get; } = 112;
    public int LocalPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    public byte ConnectionId { get; }
    public byte HelloFlags { get; }
    public byte Mode => (byte)(HelloFlags & 3);
    public RudpHello Hello { get; }
    public RudpHello? ServerHello { get; private set; }

    /// <summary>Every datagram the server sent, in order.</summary>
    public List<RudpPacket> Datagrams { get; } = [];

    /// <summary>Sends the hello and returns once the server's hello has arrived.</summary>
    public async Task<RudpPacket> HelloAsync()
    {
        SendMessage(new RudpMessage { HeaderFlags = 0x5000, Sequence = _sequence++, Payload = Hello.ToBytes() }, HelloFlags);
        using CancellationTokenSource timeout = new(5000);
        while (true)
        {
            RudpPacket packet = await ReceiveAsync(timeout.Token);
            foreach (RudpMessage message in packet.Messages)
                if (!message.Encrypted && RudpHello.TryParse(message.Payload, out RudpHello server))
                {
                    ServerHello = server;
                    _cipher = RudpCrypto.Cipher(RudpCrypto.SessionKey(server.Nonce, Hello.Nonce, _accessKey));
                    return packet;
                }
        }
    }

    /// <summary>The whole handshake: hellos, key confirmation both ways, 01 / 01, 00000000.</summary>
    public async Task HandshakeAsync()
    {
        if (ServerHello is null)
            await HelloAsync();
        SendReliable(0x1000, Hello.Nonce, encrypt: true);
        byte[] confirmation = await ExpectControlAsync(encrypted: true);
        if (!confirmation.AsSpan().SequenceEqual(ServerHello!.Nonce))
            throw new InvalidOperationException($"server confirmed with {Convert.ToHexString(confirmation)}, not its nonce");
        SendReliable(0x1000, [0x01], encrypt: false);
        byte[] one = await ExpectControlAsync(encrypted: false);
        if (one is not [0x01])
            throw new InvalidOperationException($"server answered 01 with {Convert.ToHexString(one)}");
        SendReliable(0x1000, [0, 0, 0, 0], encrypt: false);
    }

    /// <summary>Sends an RT message in an encrypted message: reliable on the stream channel, or unreliable on the control channel (as R2's pings).</summary>
    public void Send(BaseScertMessage message, bool reliable = true)
    {
        byte[] payload = [.. message.Serialize(MediusVersion, null!).SelectMany(f => f)];
        if (reliable)
            SendReliable(0x2000, payload, encrypt: true);
        else
        {
            _cipher!.Encrypt(payload, out byte[] encrypted, out byte[] hash);
            SendMessage(new RudpMessage { HeaderFlags = 0x9000, Payload = encrypted, Hash = hash }, (byte)(0x80 | Mode));
        }
    }

    public void SendApp(BaseMediusMessage message, bool reliable = true) => Send(new RT_MSG_CLIENT_APP_TOSERVER { Message = message }, reliable);

    /// <summary>Sends a raw datagram.</summary>
    public void SendPacket(RudpPacket packet) => _udp.Send(packet.ToBytes());

    /// <summary>Waits for the next RT message of type T.</summary>
    public async Task<T> ExpectAsync<T>(int timeoutMs = 5000) where T : BaseScertMessage
    {
        using CancellationTokenSource timeout = new(timeoutMs);
        while (true)
        {
            while (_received.Count > 0)
            {
                BaseScertMessage message = _received.Dequeue();
                if (message is T wanted)
                    return wanted;
                throw new InvalidOperationException($"expected {typeof(T).Name}, got {message}");
            }
            RudpPacket packet = await ReceiveAsync(timeout.Token);
            foreach (RudpMessage message in packet.Messages.Where(m => m.Encrypted && (m.HeaderFlags & 0x3000) != 0x1000))
            {
                if (message.Reliable && !_delivered.Add(message.Sequence))
                    continue;
                if (!_cipher!.Decrypt(message.Payload, message.Hash, out byte[] plain))
                    throw new InvalidOperationException($"server message {message} fails its hash check");
                _pending.AddRange(plain);
                ParseFrames();
            }
        }
    }

    public async Task<T> ExpectAppAsync<T>(int timeoutMs = 5000) where T : BaseMediusMessage
    {
        RT_MSG_SERVER_APP app = await ExpectAsync<RT_MSG_SERVER_APP>(timeoutMs);
        return app.Message as T ?? throw new InvalidOperationException($"expected {typeof(T).Name}, got {app.Message}");
    }

    /// <summary>Waits for a datagram, acks its reliable messages, and returns it.</summary>
    public async Task<RudpPacket> ReceiveAsync(CancellationToken token)
    {
        UdpReceiveResult result = await _udp.ReceiveAsync(token);
        if (!RudpPacket.TryParse(result.Buffer, out RudpPacket packet, out string? error))
            throw new InvalidOperationException($"server sent an unparsable datagram ({error}): {Convert.ToHexString(result.Buffer)}");
        Datagrams.Add(packet);
        List<ushort> acks = [.. packet.Messages.Where(m => m.Reliable).Select(m => m.Sequence)];
        if (acks.Count > 0)
            _udp.Send(new RudpPacket { ConnectionId = ConnectionId, Flags = (byte)(0x40 | Mode), Acks = acks }.ToBytes());
        return packet;
    }

    private async Task<byte[]> ExpectControlAsync(bool encrypted)
    {
        using CancellationTokenSource timeout = new(5000);
        while (true)
        {
            RudpPacket packet = await ReceiveAsync(timeout.Token);
            foreach (RudpMessage message in packet.Messages)
            {
                if ((message.HeaderFlags & 0x3000) != 0x1000 || message.Encrypted != encrypted || !message.Reliable ||
                    RudpHello.TryParse(message.Payload, out _) || !_delivered.Add(message.Sequence))
                    continue;
                if (!encrypted)
                    return message.Payload;
                if (!_cipher!.Decrypt(message.Payload, message.Hash, out byte[] plain))
                    throw new InvalidOperationException($"server confirmation {message} fails its hash check");
                return plain;
            }
        }
    }

    private void SendReliable(ushort channel, byte[] payload, bool encrypt)
    {
        RudpMessage message = new() { HeaderFlags = (ushort)(0x4000 | channel | (encrypt ? 0x8000 : 0)), Sequence = _sequence++, Payload = payload };
        if (encrypt)
        {
            _cipher!.Encrypt(payload, out byte[] encrypted, out byte[] hash);
            message.Payload = encrypted;
            message.Hash = hash;
        }
        SendMessage(message, (byte)(0x80 | Mode));
    }

    private void SendMessage(RudpMessage message, byte flags) =>
        _udp.Send(new RudpPacket { ConnectionId = ConnectionId, Flags = flags, Messages = [message] }.ToBytes());

    private void ParseFrames()
    {
        while (_pending.Count >= 3)
        {
            int length = _pending[1] | (_pending[2] << 8);
            if (_pending.Count < 3 + length)
                return;
            byte[] frame = [.. _pending.GetRange(0, 3 + length)];
            _pending.RemoveRange(0, 3 + length);
            foreach (BaseScertMessage message in ScertDatagramPeer.Parse(frame, MediusVersion, null))
                _received.Enqueue(message);
        }
    }

    public ValueTask DisposeAsync()
    {
        _udp.Dispose();
        return ValueTask.CompletedTask;
    }
}
