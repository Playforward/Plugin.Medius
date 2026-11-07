using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Plugin.Medius.Web;

/// <summary>
/// One SSL 3.0 server connection (RFC 6101), run without a socket like
/// <see cref="LegacyTlsSession"/>. Sony's rt_ssl (SCE-RT 3.03) speaks only
/// this: Killzone 2 1.29 opens with an SSLv2-format ClientHello asking for
/// SSL 3.0 with the one suite SSL_RSA_WITH_RC4_128_MD5 (80 1C 01 03 00 ..
/// 00 00 04) [live 2026-10-31], which BouncyCastle 2.x no longer offers.
/// </summary>
/// <remarks>
/// RSA key exchange with the <see cref="LegacyTlsCredentials"/> key, RC4-128
/// with MD5 (0x0004) or SHA (0x0005) MACs, no session resumption. A ClientHello
/// in an SSL 3.0 record is accepted as well as the SSLv2 form.
/// </remarks>
public sealed class Ssl3Session : IWebTlsSession
{
    private const byte ChangeCipherSpec = 20, Alert = 21, Handshake = 22, ApplicationData = 23;
    private const byte ClientHello = 1, ServerHello = 2, CertificateMessage = 11, ServerHelloDone = 14, ClientKeyExchange = 16, Finished = 20;
    private static readonly byte[] Version = [3, 0];

    /// <summary>The cipher suites served, best first.</summary>
    public static readonly ushort[] Suites = [0x0005, 0x0004];

    private readonly LegacyTlsCredentials _credentials;
    private readonly List<byte> _input = [];
    private readonly MemoryStream _output = new();
    private readonly MemoryStream _handshakeMessages = new();
    private byte[] _clientRandom = [], _serverRandom = [], _master = [];
    private ushort _suite;
    private CipherState? _read, _write;
    private State _state = State.Hello;

    private enum State { Hello, KeyExchange, ChangeCipherSpec, Finished, Open, Closed }

    public Ssl3Session(LegacyTlsCredentials credentials) => _credentials = credentials;

    public bool HandshakeComplete => _state == State.Open;
    public bool IsClosed => _state == State.Closed;
    public string Negotiated => $"SSL 3.0, cipher suite 0x{_suite:X4}";

    /// <summary>The last alert the client sent (level, description), for logs.</summary>
    public (byte Level, byte Description)? LastAlert { get; private set; }

    /// <summary>True when <paramref name="data"/> opens with an SSLv2-format ClientHello or an SSL 3.0 handshake record.</summary>
    public static bool IsHello(ReadOnlySpan<byte> data) =>
        data.Length >= 5 && (((data[0] & 0x80) != 0 && data[2] == ClientHello && data[3] == 3) || (data[0] == Handshake && data[1] == 3 && data[2] == 0));

    public byte[] Receive(ReadOnlySpan<byte> data)
    {
        _input.AddRange(data.ToArray());
        MemoryStream plain = new();
        while (_state != State.Closed)
        {
            if (_state == State.Hello && _input.Count >= 2 && (_input[0] & 0x80) != 0)
            {
                int length = ((_input[0] & 0x7F) << 8) | _input[1];
                if (_input.Count < 2 + length)
                    break;
                byte[] message = [.. _input.GetRange(2, length)];
                _input.RemoveRange(0, 2 + length);
                ReadV2Hello(message);
                continue;
            }
            if (_input.Count < 5)
                break;
            int recordLength = BinaryPrimitives.ReadUInt16BigEndian([_input[3], _input[4]]);
            if (_input.Count < 5 + recordLength)
                break;
            byte type = _input[0];
            byte[] fragment = [.. _input.GetRange(5, recordLength)];
            _input.RemoveRange(0, 5 + recordLength);
            if (_read is not null)
                fragment = _read.Open(type, fragment);
            switch (type)
            {
                case Handshake: ReadHandshake(fragment); break;
                case ChangeCipherSpec:
                    Expect(State.ChangeCipherSpec);
                    _read = new CipherState(_suite, _master, _clientRandom, _serverRandom, client: true);
                    _state = State.Finished;
                    break;
                case Alert:
                    LastAlert = fragment.Length >= 2 ? (fragment[0], fragment[1]) : null;
                    _state = State.Closed;
                    break;
                case ApplicationData:
                    Expect(State.Open);
                    plain.Write(fragment);
                    break;
                default: throw new InvalidDataException($"SSL record type {type}");
            }
        }
        return plain.ToArray();
    }

    public void Send(ReadOnlySpan<byte> data)
    {
        Expect(State.Open);
        for (int offset = 0; offset < data.Length; offset += 16384)
            WriteRecord(ApplicationData, data.Slice(offset, Math.Min(16384, data.Length - offset)).ToArray());
    }

    public byte[] TakeOutput()
    {
        byte[] bytes = _output.ToArray();
        _output.SetLength(0);
        return bytes;
    }

    /// <summary>
    /// CLIENT-HELLO: version, cipher spec / session id / challenge lengths, then
    /// those. The challenge, zero-padded on the left to 32 bytes, is the client
    /// random; the message itself (after the 2-byte header) is hashed as the
    /// first handshake message (RFC 6101 E.1).
    /// </summary>
    private void ReadV2Hello(byte[] message)
    {
        if (message.Length < 9 || message[0] != ClientHello)
            throw new InvalidDataException("not an SSLv2 CLIENT-HELLO");
        int specs = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(3)), sessionId = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(5)),
            challenge = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(7));
        List<ushort> offered = [];
        for (int i = 0; i + 3 <= specs; i += 3)
            if (message[9 + i] == 0)
                offered.Add(BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(10 + i)));
        byte[] random = new byte[32];
        message.AsSpan(9 + specs + sessionId, challenge).CopyTo(random.AsSpan(32 - Math.Min(32, challenge)));
        _handshakeMessages.Write(message);
        Hello(random, offered);
    }

    private void ReadHandshake(byte[] fragment)
    {
        for (int offset = 0; offset + 4 <= fragment.Length;)
        {
            byte type = fragment[offset];
            int length = (fragment[offset + 1] << 16) | (fragment[offset + 2] << 8) | fragment[offset + 3];
            byte[] whole = fragment[offset..(offset + 4 + length)];
            byte[] body = whole[4..];
            offset += 4 + length;
            switch (type)
            {
                case ClientHello:
                {
                    Expect(State.Hello);
                    _handshakeMessages.Write(whole);
                    int at = 2 + 32;
                    at += 1 + body[at];
                    int suites = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(at));
                    List<ushort> offered = [];
                    for (int i = 0; i < suites; i += 2)
                        offered.Add(BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(at + 2 + i)));
                    Hello(body[2..34], offered);
                    break;
                }
                case ClientKeyExchange:
                    Expect(State.KeyExchange);
                    _handshakeMessages.Write(whole);
                    KeyExchange(body);
                    _state = State.ChangeCipherSpec;
                    break;
                case Finished:
                {
                    Expect(State.Finished);
                    byte[] expected = FinishedHash("CLNT"u8);
                    if (!CryptographicOperations.FixedTimeEquals(expected, body))
                        throw new InvalidDataException("client Finished does not verify");
                    _handshakeMessages.Write(whole);
                    byte[] serverFinished = FinishedHash("SRVR"u8);
                    WriteRecord(ChangeCipherSpec, [1]);
                    _write = new CipherState(_suite, _master, _clientRandom, _serverRandom, client: false);
                    WriteHandshake(Finished, serverFinished);
                    _state = State.Open;
                    break;
                }
                default: throw new InvalidDataException($"SSL handshake message {type} in state {_state}");
            }
        }
    }

    /// <summary>ServerHello, Certificate, ServerHelloDone.</summary>
    private void Hello(byte[] clientRandom, List<ushort> offered)
    {
        _clientRandom = clientRandom;
        _suite = Suites.FirstOrDefault(offered.Contains);
        if (_suite == 0)
            throw new InvalidDataException($"no common cipher suite (offered {string.Join(", ", offered.Select(s => $"0x{s:X4}"))})");
        _serverRandom = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(_serverRandom, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        RandomNumberGenerator.Fill(_serverRandom.AsSpan(4));

        MemoryStream hello = new();
        hello.Write(Version);
        hello.Write(_serverRandom);
        hello.WriteByte(0); // no session id: nothing to resume
        hello.WriteByte((byte)(_suite >> 8));
        hello.WriteByte((byte)_suite);
        hello.WriteByte(0); // null compression
        WriteHandshake(ServerHello, hello.ToArray());

        byte[] der = _credentials.Certificate.GetEncoded();
        byte[] certificates = new byte[6 + der.Length];
        WriteU24(certificates, 0, 3 + der.Length);
        WriteU24(certificates, 3, der.Length);
        der.CopyTo(certificates, 6);
        WriteHandshake(CertificateMessage, certificates);
        WriteHandshake(ServerHelloDone, []);
        _state = State.KeyExchange;
    }

    /// <summary>
    /// The RSA-encrypted pre-master secret, bare in SSL 3.0 (some clients add
    /// TLS's 2-byte length anyway). A pre-master that does not decrypt is
    /// replaced by random bytes, so the failure shows at Finished (RFC 5246 7.4.7.1).
    /// </summary>
    private void KeyExchange(byte[] body)
    {
        int modulusBytes = (_credentials.PrivateKey.Modulus.BitLength + 7) / 8;
        byte[] encrypted = body.Length == modulusBytes + 2 && BinaryPrimitives.ReadUInt16BigEndian(body) == modulusBytes ? body[2..] : body;
        byte[] preMaster;
        try
        {
            Pkcs1Encoding rsa = new(new RsaEngine());
            rsa.Init(false, _credentials.PrivateKey);
            preMaster = rsa.ProcessBlock(encrypted, 0, encrypted.Length);
            if (preMaster.Length != 48)
                throw new InvalidCipherTextException("pre-master is not 48 bytes");
        }
        catch (InvalidCipherTextException)
        {
            preMaster = RandomNumberGenerator.GetBytes(48);
        }
        _master = Derive(preMaster, _clientRandom, _serverRandom, 48);
    }

    /// <summary>SSL 3.0's key derivation: MD5(secret + SHA1("A" + secret + a + b)) || MD5(secret + SHA1("BB" + ...)) || ...</summary>
    public static byte[] Derive(byte[] secret, byte[] first, byte[] second, int length)
    {
        byte[] result = new byte[length];
        for (int i = 0, offset = 0; offset < length; i++, offset += 16)
        {
            byte[] label = Enumerable.Repeat((byte)('A' + i), i + 1).ToArray();
            byte[] inner = SHA1.HashData([.. label, .. secret, .. first, .. second]);
            byte[] block = MD5.HashData([.. secret, .. inner]);
            block.AsSpan(0, Math.Min(16, length - offset)).CopyTo(result.AsSpan(offset));
        }
        return result;
    }

    /// <summary>Finished: MD5 and SHA-1 of the handshake so far, the sender and the master secret, padded (RFC 6101 5.6.9).</summary>
    private byte[] FinishedHash(ReadOnlySpan<byte> sender)
    {
        byte[] messages = _handshakeMessages.ToArray();
        byte[] md5Inner = MD5.HashData([.. messages, .. sender, .. _master, .. Pad(0x36, 48)]);
        byte[] md5 = MD5.HashData([.. _master, .. Pad(0x5C, 48), .. md5Inner]);
        byte[] shaInner = SHA1.HashData([.. messages, .. sender, .. _master, .. Pad(0x36, 40)]);
        byte[] sha = SHA1.HashData([.. _master, .. Pad(0x5C, 40), .. shaInner]);
        return [.. md5, .. sha];
    }

    private void WriteHandshake(byte type, byte[] body)
    {
        byte[] message = new byte[4 + body.Length];
        message[0] = type;
        WriteU24(message, 1, body.Length);
        body.CopyTo(message, 4);
        _handshakeMessages.Write(message);
        WriteRecord(Handshake, message);
    }

    private void WriteRecord(byte type, byte[] content)
    {
        byte[] fragment = _write?.Seal(type, content) ?? content;
        _output.WriteByte(type);
        _output.Write(Version);
        _output.WriteByte((byte)(fragment.Length >> 8));
        _output.WriteByte((byte)fragment.Length);
        _output.Write(fragment);
    }

    private void Expect(State state)
    {
        if (_state != state)
            throw new InvalidDataException($"SSL message out of order (expected {state}, in {_state})");
    }

    private static void WriteU24(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 16);
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)value;
    }

    private static byte[] Pad(byte value, int count) => Enumerable.Repeat(value, count).ToArray();

    /// <summary>One direction's RC4 key, MAC secret and sequence number.</summary>
    private sealed class CipherState
    {
        private readonly RC4Engine _rc4 = new();
        private readonly byte[] _macSecret;
        private readonly bool _sha;
        private ulong _sequence;

        /// <summary>Key block (RFC 6101 6.2.2): client MAC, server MAC, client key, server key.</summary>
        public CipherState(ushort suite, byte[] master, byte[] clientRandom, byte[] serverRandom, bool client)
        {
            _sha = suite == 0x0005;
            int macLength = _sha ? 20 : 16;
            byte[] block = Derive(master, serverRandom, clientRandom, 2 * macLength + 32);
            _macSecret = block[(client ? 0 : macLength)..((client ? 0 : macLength) + macLength)];
            int keyAt = 2 * macLength + (client ? 0 : 16);
            _rc4.Init(true, new Org.BouncyCastle.Crypto.Parameters.KeyParameter(block[keyAt..(keyAt + 16)]));
        }

        public byte[] Seal(byte type, byte[] content)
        {
            byte[] record = [.. content, .. Mac(type, content)];
            byte[] output = new byte[record.Length];
            _rc4.ProcessBytes(record, 0, record.Length, output, 0);
            return output;
        }

        public byte[] Open(byte type, byte[] fragment)
        {
            byte[] record = new byte[fragment.Length];
            _rc4.ProcessBytes(fragment, 0, fragment.Length, record, 0);
            int macLength = _sha ? 20 : 16;
            if (record.Length < macLength)
                throw new InvalidDataException("SSL record shorter than its MAC");
            byte[] content = record[..^macLength];
            if (!CryptographicOperations.FixedTimeEquals(Mac(type, content), record.AsSpan(record.Length - macLength)))
                throw new InvalidDataException("SSL record MAC does not verify");
            return content;
        }

        /// <summary>hash(secret + pad2 + hash(secret + pad1 + seq + type + length + content)) (RFC 6101 5.2.3.1).</summary>
        private byte[] Mac(byte type, byte[] content)
        {
            int padLength = _sha ? 40 : 48;
            byte[] header = new byte[11];
            BinaryPrimitives.WriteUInt64BigEndian(header, _sequence++);
            header[8] = type;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(9), (ushort)content.Length);
            byte[] innerInput = [.. _macSecret, .. Pad(0x36, padLength), .. header, .. content];
            byte[] inner = _sha ? SHA1.HashData(innerInput) : MD5.HashData(innerInput);
            byte[] outerInput = [.. _macSecret, .. Pad(0x5C, padLength), .. inner];
            return _sha ? SHA1.HashData(outerInput) : MD5.HashData(outerInput);
        }
    }
}
