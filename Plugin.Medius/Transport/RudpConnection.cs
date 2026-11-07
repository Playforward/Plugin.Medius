using NetCoreServer;
using Plugin.Medius.RT.Cryptography;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace Plugin.Medius.Transport;

/// <summary>
/// One client's SCE-RT RUdp connection (<see cref="RudpPacket"/>), as a transport
/// for a <see cref="ScertConnection"/>: RT frames travel plain inside encrypted
/// RUdp messages. The sequence, read from PSRewired's Resistance 2 server
/// (MediusProxy capture 2026-10-29, every message decrypted with
/// <see cref="RudpCrypto"/>) [live]:
/// <code>
/// C hello (ch1, nonce)        -> S hello (ch1, own nonce, client's flags), ack
/// C Enc(client nonce) (ch1)   -> S Enc(server nonce) (ch1)       key confirmation
/// C 01 (ch1)                  -> S 01 (ch1)
/// C 00000000 (ch1)                                               connection open
/// then RT frames: reliable on ch2 (and ch0), unreliable on ch0/ch1; the server
/// sends everything reliable on ch2.
/// C two empty unreliable      -> S the same                      keep-alive
/// </code>
/// "ch" is the message header's bits 0x3000 [live; meaning guess]. Reliable
/// messages carry one sequence number per direction across channels and are
/// delivered in that order.
/// </summary>
public sealed class RudpConnection : IScertTransport
{
    public const ushort Control = 0x1000;
    public const ushort Stream = 0x2000;

    /// <summary>Largest payload put in one message; the header's length field holds 12 bits.</summary>
    private const int MaxPayload = 1024;
    private const int MaxBufferedOutOfOrder = 512;
    private const int RttSamples = 32;
    private static readonly TimeSpan ResendAfter = TimeSpan.FromMilliseconds(500);
    private const int MaxResends = 30;

    private readonly object _lock = new();
    private readonly UdpServer? _server;
    private readonly Action<byte[]>? _sendRaw;
    private readonly PS3_RCQ _cipher;
    private readonly Dictionary<ushort, RudpMessage> _outOfOrder = [];
    private readonly Dictionary<ushort, Unacked> _unacked = [];
    private readonly Queue<int> _rtt = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private ushort _nextSequence;
    private RudpMessage? _hello;
    private ushort _expected;

    public RudpConnection(UdpServer server, IPEndPoint remote, RudpPacket hello, RudpHello clientHello, ushort clientSequence, string accessKey)
        : this(remote, server.Port, hello, clientHello, clientSequence, accessKey)
    {
        _server = server;
    }

    /// <summary>For tests: datagrams go to <paramref name="sendRaw"/>.</summary>
    internal RudpConnection(IPEndPoint remote, int localPort, RudpPacket hello, RudpHello clientHello, ushort clientSequence, string accessKey, Action<byte[]> sendRaw)
        : this(remote, localPort, hello, clientHello, clientSequence, accessKey)
    {
        _sendRaw = sendRaw;
    }

    private RudpConnection(IPEndPoint remote, int localPort, RudpPacket hello, RudpHello clientHello, ushort clientSequence, string accessKey)
    {
        Remote = remote;
        LocalPort = localPort;
        ConnectionId = hello.ConnectionId;
        HelloFlags = hello.Flags;
        ClientHello = clientHello;
        ClientHelloSequence = clientSequence;
        AccessKey = accessKey;
        ServerHello = new RudpHello { Prefix = clientHello.Prefix, Id = clientHello.Id, Nonce = RandomNumberGenerator.GetBytes(16) };
        _cipher = RudpCrypto.Cipher(RudpCrypto.SessionKey(ServerHello.Nonce, clientHello.Nonce, accessKey));
        _expected = (ushort)(clientSequence + 1);
        _nextSequence = (ushort)RandomNumberGenerator.GetInt32(0x10000);
        LastReceived = DateTime.UtcNow;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public IPEndPoint Remote { get; }
    IPEndPoint? IScertTransport.Remote => Remote;
    public int LocalPort { get; }
    public bool RtEncryption => false;

    public byte ConnectionId { get; }

    /// <summary>
    /// The client hello's flags. The server echoes them in its hello, and their low two
    /// bits in every packet (client 0x88 -> acks 0x40, 0x8B -> 0x43, 0x89 -> 0x41;
    /// answering 0x8B to a 0x89 hello left the client retrying) [live].
    /// </summary>
    public byte HelloFlags { get; }
    public byte Mode => (byte)(HelloFlags & 0x03);

    public RudpHello ClientHello { get; }
    public ushort ClientHelloSequence { get; }
    public RudpHello ServerHello { get; }
    public string AccessKey { get; }

    public RudpState State { get; private set; } = RudpState.Handshake;
    public DateTime LastReceived { get; private set; }
    public DateTime? ClosedUtc { get; private set; }

    /// <summary>The RT connection this transport carries; set by whoever accepts the connection.</summary>
    public ScertConnection? Scert { get; set; }

    /// <summary>Raised once, when the connection closes for any reason.</summary>
    public event Action<RudpConnection, string>? Closed;

    /// <summary>Starts the connection: our hello (repeated for each client hello until acked) and the ack of theirs.</summary>
    public void Start()
    {
        lock (_lock)
            SendHello();
    }

    /// <summary>
    /// Takes one datagram from the client and returns the RT bytes it completes,
    /// in order. Control messages are answered here.
    /// </summary>
    public List<byte[]> Receive(RudpPacket packet)
    {
        List<byte[]> delivered = [];
        lock (_lock)
        {
            LastReceived = DateTime.UtcNow;
            foreach (ushort ack in packet.Acks)
                Acknowledged(ack);

            List<ushort> acks = [];
            int keepAlives = 0;
            foreach (RudpMessage message in packet.Messages)
            {
                if (!message.Reliable)
                {
                    if (message.Payload.Length == 0 && !message.Encrypted)
                        keepAlives++;
                    else if (State != RudpState.Closed)
                        Deliver(message, delivered);
                    continue;
                }

                acks.Add(message.Sequence);
                if (message.Sequence == ClientHelloSequence && !message.Encrypted && RudpHello.TryParse(message.Payload, out _))
                {
                    if (State == RudpState.Handshake)
                        SendHello();
                    continue;
                }
                short ahead = (short)(message.Sequence - _expected);
                if (ahead < 0 || State == RudpState.Closed)
                    continue;
                if (ahead > 0)
                {
                    if (_outOfOrder.Count < MaxBufferedOutOfOrder)
                        _outOfOrder.TryAdd(message.Sequence, message);
                    continue;
                }
                Deliver(message, delivered);
                _expected++;
                while (_outOfOrder.Remove(_expected, out RudpMessage? next))
                {
                    Deliver(next, delivered);
                    _expected++;
                }
            }
            if (acks.Count > 0)
                SendDatagram(new RudpPacket { ConnectionId = ConnectionId, Flags = (byte)(0x40 | Mode), Acks = acks });
            if (keepAlives > 0 && packet.Messages.Count == keepAlives)
                SendDatagram(new RudpPacket
                {
                    ConnectionId = ConnectionId,
                    Flags = Mode,
                    Messages = [.. Enumerable.Range(0, keepAlives).Select(_ => new RudpMessage())],
                });
        }
        return delivered;
    }

    /// <summary>Sends RT frames: packed into reliable, encrypted stream messages.</summary>
    public void Send(IReadOnlyList<byte[]> frames)
    {
        List<byte> payload = [];
        lock (_lock)
        {
            if (State == RudpState.Closed)
                return;
            foreach (byte[] frame in frames)
            {
                if (payload.Count > 0 && payload.Count + frame.Length > MaxPayload)
                {
                    SendReliable(Stream, [.. payload], encrypt: true);
                    payload.Clear();
                }
                payload.AddRange(frame);
            }
            if (payload.Count > 0)
                SendReliable(Stream, [.. payload], encrypt: true);
        }
    }

    public void Disconnect() => Close("closed by the server");

    /// <summary>Resends what the client has not acknowledged; closes a connection that stopped answering.</summary>
    public void Tick(TimeSpan idleTimeout)
    {
        string? reason = null;
        lock (_lock)
        {
            if (State == RudpState.Closed)
                return;
            TimeSpan now = _clock.Elapsed;
            foreach (Unacked pending in _unacked.Values)
            {
                if (now - pending.LastSent < ResendAfter)
                    continue;
                if (++pending.Resends > MaxResends)
                {
                    reason = $"no ack for #{pending.Message.Sequence:X4} after {MaxResends} resends";
                    break;
                }
                pending.LastSent = now;
                pending.Resent = true;
                SendDatagram(Packet(pending.Message));
            }
            if (reason is null && DateTime.UtcNow - LastReceived > idleTimeout)
                reason = $"nothing received for {idleTimeout.TotalSeconds:0} s";
        }
        if (reason is not null)
            Close(reason);
    }

    /// <summary>Round trips of acknowledged first sends, in milliseconds: max, min, average (zeros before the first).</summary>
    public (ushort Max, ushort Min, ushort Average) Latency()
    {
        lock (_lock)
        {
            if (_rtt.Count == 0)
                return (0, 0, 0);
            return ((ushort)_rtt.Max(), (ushort)_rtt.Min(), (ushort)_rtt.Average());
        }
    }

    public override string ToString() => $"{Remote} conn {ConnectionId:X2} id {Convert.ToHexString(ClientHello.Id)}";

    private void Close(string reason)
    {
        lock (_lock)
        {
            if (State == RudpState.Closed)
                return;
            State = RudpState.Closed;
            ClosedUtc = DateTime.UtcNow;
            _unacked.Clear();
            _outOfOrder.Clear();
        }
        Closed?.Invoke(this, reason);
    }

    private void Deliver(RudpMessage message, List<byte[]> delivered)
    {
        if (message.Encrypted)
        {
            if (!_cipher.Decrypt(message.Payload, message.Hash, out byte[] plain))
            {
                Log.Warning("[medius/RUDP] {Connection}: message {Message} fails its hash check; dropped", this, message);
                return;
            }
            if (message.Reliable && (message.HeaderFlags & 0x3000) == Control && State == RudpState.Handshake)
                Confirm(plain);
            else if (plain.Length > 0)
                delivered.Add(plain);
            return;
        }

        if ((message.HeaderFlags & 0x3000) != Control || message.Payload.Length == 0)
            return;
        // Plain control after the key confirmation: the client's 01 is answered with 01,
        // its 00000000 opens the connection [live].
        if (message.Payload is [0x01])
            SendReliable(Control, [0x01], encrypt: false);
        else if (message.Payload.Length == 4 && State == RudpState.Confirmed)
            State = RudpState.Open;
    }

    /// <summary>The client's key confirmation is its own nonce; ours goes back the same way.</summary>
    private void Confirm(byte[] plain)
    {
        if (!plain.AsSpan().SequenceEqual(ClientHello.Nonce))
        {
            Log.Warning("[medius/RUDP] {Connection}: key confirmation {Plain} is not the client's nonce {Nonce}",
                this, Convert.ToHexString(plain), Convert.ToHexString(ClientHello.Nonce));
            return;
        }
        State = RudpState.Confirmed;
        SendReliable(Control, ServerHello.Nonce, encrypt: true);
    }

    /// <summary>Our hello, repeated while unacknowledged, and the ack of the client's (repeated whenever it repeats).</summary>
    private void SendHello()
    {
        if (_hello is null)
        {
            _hello = new RudpMessage { HeaderFlags = 0x4000 | Control, Sequence = _nextSequence++, Payload = ServerHello.ToBytes() };
            _unacked[_hello.Sequence] = new Unacked(_hello, _clock.Elapsed);
            SendDatagram(Packet(_hello));
        }
        else if (_unacked.TryGetValue(_hello.Sequence, out Unacked? pending))
        {
            pending.LastSent = _clock.Elapsed;
            pending.Resent = true;
            SendDatagram(Packet(_hello));
        }
        SendDatagram(new RudpPacket { ConnectionId = ConnectionId, Flags = (byte)(0x40 | Mode), Acks = [ClientHelloSequence] });
    }

    private void SendReliable(ushort channel, byte[] payload, bool encrypt)
    {
        RudpMessage message = new() { HeaderFlags = (ushort)(0x4000 | channel | (encrypt ? 0x8000 : 0)), Sequence = _nextSequence++, Payload = payload };
        if (encrypt)
        {
            _cipher.Encrypt(payload, out byte[] encrypted, out byte[] hash);
            message.Payload = encrypted;
            message.Hash = hash;
        }
        _unacked[message.Sequence] = new Unacked(message, _clock.Elapsed);
        SendDatagram(Packet(message));
    }

    /// <summary>
    /// A data packet: 0x80, the connecting bit 0x08 until the connection is open, the
    /// mode bits; the server's hello repeats the client's flags whole [live].
    /// </summary>
    private RudpPacket Packet(RudpMessage message)
    {
        byte flags = message == _hello
            ? HelloFlags
            : (byte)(0x80 | (State is RudpState.Open ? 0 : 0x08) | Mode);
        return new RudpPacket { ConnectionId = ConnectionId, Flags = flags, Messages = [message] };
    }

    private void Acknowledged(ushort sequence)
    {
        if (!_unacked.Remove(sequence, out Unacked? pending))
            return;
        if (pending.Resent)
            return;
        _rtt.Enqueue((int)Math.Min(ushort.MaxValue, (_clock.Elapsed - pending.FirstSent).TotalMilliseconds));
        while (_rtt.Count > RttSamples)
            _rtt.Dequeue();
    }

    private void SendDatagram(RudpPacket packet)
    {
        byte[] bytes = packet.ToBytes();
        if (_sendRaw is not null)
            _sendRaw(bytes);
        else
            _server!.SendAsync(Remote, bytes);
    }

    private sealed class Unacked(RudpMessage message, TimeSpan sent)
    {
        public RudpMessage Message { get; } = message;
        public TimeSpan FirstSent { get; } = sent;
        public TimeSpan LastSent { get; set; } = sent;
        public int Resends { get; set; }
        /// <summary>Sent more than once: its ack says nothing about the round trip.</summary>
        public bool Resent { get; set; }
    }
}

public enum RudpState
{
    /// <summary>Hellos exchanged, waiting for the client's key confirmation.</summary>
    Handshake,
    /// <summary>Keys confirmed both ways, control exchange (01, 00000000) under way.</summary>
    Confirmed,
    Open,
    Closed,
}
