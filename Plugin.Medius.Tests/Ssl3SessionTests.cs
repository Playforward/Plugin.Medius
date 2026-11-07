using Org.BouncyCastle.Crypto.Encodings;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Plugin.Medius.Web;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Plugin.Medius.Tests;

/// <summary>
/// <see cref="Ssl3Session"/> against a small SSL 3.0 client written here from
/// RFC 6101, opening the way Killzone 2's rt_ssl does: an SSLv2-format
/// ClientHello for SSL 3.0 offering only SSL_RSA_WITH_RC4_128_MD5 [live].
/// </summary>
public sealed class Ssl3SessionTests
{
    private static readonly LegacyTlsCredentials Credentials = LegacyTlsCredentials.Create("ssl3.test", 1024);

    [Theory]
    [InlineData((ushort)0x0004)]
    [InlineData((ushort)0x0005)]
    public void Sslv2_hello_handshake_then_application_data_both_ways(ushort suite)
    {
        Ssl3Session server = new(Credentials);
        Client client = new(suite);

        byte[] hello = client.V2Hello();
        Assert.True(Ssl3Session.IsHello(hello));
        Assert.Empty(server.Receive(hello));
        Assert.Empty(server.Receive(client.KeyExchangeFlight(server.TakeOutput())));
        client.ReadServerFinished(server.TakeOutput());
        Assert.True(server.HandshakeComplete);
        Assert.Equal($"SSL 3.0, cipher suite 0x{suite:X4}", server.Negotiated);

        Assert.Equal("GET / HTTP/1.0\r\n\r\n", Encoding.ASCII.GetString(server.Receive(client.Seal(23, "GET / HTTP/1.0\r\n\r\n"u8.ToArray()))));
        server.Send("HTTP/1.0 200 OK\r\n\r\n"u8);
        Assert.Equal("HTTP/1.0 200 OK\r\n\r\n", Encoding.ASCII.GetString(client.OpenRecords(server.TakeOutput())));
    }

    [Fact]
    public void Live_killzone2_hello_prefix_is_recognised()
    {
        // The first bytes Killzone 2 1.29 sent to the WEB listener [live 2026-10-31].
        Assert.True(Ssl3Session.IsHello(Convert.FromHexString("801C0103000003000000100000045287742C813C6DD701A9A11C95FE8DF1")));
        Assert.False(Ssl3Session.IsHello("GET / HTTP/1.0\r\n"u8));
    }

    /// <summary>The client side, from RFC 6101 (RSA, RC4-128, MD5 or SHA MAC).</summary>
    private sealed class Client(ushort suite)
    {
        private readonly MemoryStream _messages = new();
        private readonly byte[] _clientRandom = RandomNumberGenerator.GetBytes(32);
        private byte[] _serverRandom = [], _master = [], _key = [];
        private RC4Engine? _write, _read;
        private ulong _writeSeq, _readSeq;
        private bool Sha => suite == 0x0005;
        private int MacLength => Sha ? 20 : 16;

        public byte[] V2Hello()
        {
            // Challenge = the whole 32-byte random (no left padding needed).
            byte[] message = [1, 3, 0, 0, 3, 0, 0, 0, 32, 0, (byte)(suite >> 8), (byte)suite, .. _clientRandom];
            _messages.Write(message);
            return [(byte)(0x80 | (message.Length >> 8)), (byte)message.Length, .. message];
        }

        /// <summary>Reads ServerHello, Certificate, ServerHelloDone; answers ClientKeyExchange, ChangeCipherSpec, Finished.</summary>
        public byte[] KeyExchangeFlight(byte[] flight)
        {
            byte[] certificate = [];
            foreach ((byte type, byte[] body, byte[] whole) in Handshakes(Records(flight)))
            {
                _messages.Write(whole);
                if (type == 2)
                {
                    _serverRandom = body[2..34];
                    Assert.Equal(suite, BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(35)));
                }
                if (type == 11)
                    certificate = body[6..];
            }
            RsaKeyParameters key = (RsaKeyParameters)new Org.BouncyCastle.X509.X509CertificateParser().ReadCertificate(certificate).GetPublicKey();
            byte[] preMaster = [3, 0, .. RandomNumberGenerator.GetBytes(46)];
            Pkcs1Encoding rsa = new(new RsaEngine());
            rsa.Init(true, new ParametersWithRandom(key, new SecureRandom()));
            byte[] encrypted = rsa.ProcessBlock(preMaster, 0, preMaster.Length);
            byte[] keyExchange = Handshake(16, encrypted);

            _master = Kdf(preMaster, _clientRandom, _serverRandom, 48);
            _key = Kdf(_master, _serverRandom, _clientRandom, 2 * MacLength + 32);
            byte[] finished = Handshake(20, Finished("CLNT"u8));
            MemoryStream output = new();
            output.Write(Record(22, keyExchange));
            output.Write(Record(20, [1]));
            _write = Rc4(_key[(2 * MacLength)..(2 * MacLength + 16)]);
            output.Write(Seal(22, finished));
            return output.ToArray();
        }

        public void ReadServerFinished(byte[] flight)
        {
            List<(byte Type, byte[] Body)> records = Records(flight);
            Assert.Equal(20, records[0].Type);
            _read = Rc4(_key[(2 * MacLength + 16)..(2 * MacLength + 32)]);
            byte[] finished = Open(records[1].Type, records[1].Body);
            Assert.Equal(Finished("SRVR"u8), finished[4..]);
        }

        public byte[] OpenRecords(byte[] bytes) => [.. Records(bytes).SelectMany(r => Open(r.Type, r.Body))];

        public byte[] Seal(byte type, byte[] content)
        {
            byte[] plain = [.. content, .. Mac(_key[..MacLength], ref _writeSeq, type, content)];
            byte[] sealedBytes = new byte[plain.Length];
            _write!.ProcessBytes(plain, 0, plain.Length, sealedBytes, 0);
            return Record(type, sealedBytes);
        }

        private byte[] Open(byte type, byte[] fragment)
        {
            byte[] plain = new byte[fragment.Length];
            _read!.ProcessBytes(fragment, 0, fragment.Length, plain, 0);
            byte[] content = plain[..^MacLength];
            Assert.Equal(Mac(_key[MacLength..(2 * MacLength)], ref _readSeq, type, content), plain[^MacLength..]);
            return content;
        }

        private byte[] Mac(byte[] secret, ref ulong sequence, byte type, byte[] content)
        {
            int pad = Sha ? 40 : 48;
            byte[] header = new byte[11];
            BinaryPrimitives.WriteUInt64BigEndian(header, sequence++);
            header[8] = type;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(9), (ushort)content.Length);
            Func<byte[], byte[]> hash = Sha ? SHA1.HashData : MD5.HashData;
            return hash([.. secret, .. Enumerable.Repeat((byte)0x5C, pad), .. hash([.. secret, .. Enumerable.Repeat((byte)0x36, pad), .. header, .. content])]);
        }

        private byte[] Finished(ReadOnlySpan<byte> sender)
        {
            byte[] m = _messages.ToArray(), s = sender.ToArray();
            byte[] md5 = MD5.HashData([.. _master, .. Enumerable.Repeat((byte)0x5C, 48), .. MD5.HashData([.. m, .. s, .. _master, .. Enumerable.Repeat((byte)0x36, 48)])]);
            byte[] sha = SHA1.HashData([.. _master, .. Enumerable.Repeat((byte)0x5C, 40), .. SHA1.HashData([.. m, .. s, .. _master, .. Enumerable.Repeat((byte)0x36, 40)])]);
            return [.. md5, .. sha];
        }

        private byte[] Handshake(byte type, byte[] body)
        {
            byte[] whole = [type, (byte)(body.Length >> 16), (byte)(body.Length >> 8), (byte)body.Length, .. body];
            _messages.Write(whole);
            return whole;
        }

        private static byte[] Kdf(byte[] secret, byte[] a, byte[] b, int length)
        {
            List<byte> output = [];
            for (int i = 0; output.Count < length; i++)
                output.AddRange(MD5.HashData([.. secret, .. SHA1.HashData([.. Enumerable.Repeat((byte)('A' + i), i + 1), .. secret, .. a, .. b])]));
            return [.. output.Take(length)];
        }

        private static RC4Engine Rc4(byte[] key)
        {
            RC4Engine rc4 = new();
            rc4.Init(true, new KeyParameter(key));
            return rc4;
        }

        private static byte[] Record(byte type, byte[] fragment) => [type, 3, 0, (byte)(fragment.Length >> 8), (byte)fragment.Length, .. fragment];

        private static List<(byte Type, byte[] Body)> Records(byte[] bytes)
        {
            List<(byte, byte[])> records = [];
            for (int at = 0; at < bytes.Length;)
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 3));
                Assert.Equal(new byte[] { 3, 0 }, bytes[(at + 1)..(at + 3)]);
                records.Add((bytes[at], bytes[(at + 5)..(at + 5 + length)]));
                at += 5 + length;
            }
            return records;
        }

        private static IEnumerable<(byte Type, byte[] Body, byte[] Whole)> Handshakes(List<(byte Type, byte[] Body)> records)
        {
            foreach ((byte type, byte[] body) in records)
            {
                Assert.Equal(22, type);
                for (int at = 0; at < body.Length;)
                {
                    int length = (body[at + 1] << 16) | (body[at + 2] << 8) | body[at + 3];
                    yield return (body[at], body[(at + 4)..(at + 4 + length)], body[at..(at + 4 + length)]);
                    at += 4 + length;
                }
            }
        }
    }
}
