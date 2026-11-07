using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Org.BouncyCastle.X509;

namespace Plugin.Medius.Web;

/// <summary>
/// The server certificate and key every <see cref="LegacyTlsSession"/> presents:
/// self-signed RSA, made on first start and kept in the data directory so a
/// client that pins nothing sees the same certificate each run.
/// </summary>
/// <remarks>
/// PS3 games verify through a callback of their own, and the ones seen so far
/// accept any chain (Resistance 2: cellHttpClientSetSslCallback with a callback
/// that returns 0, 1.60 0x266634 [C]). SHA-1 signs the certificate because 2008
/// SSL libraries may not know SHA-256 [guess].
/// </remarks>
public sealed class LegacyTlsCredentials
{
    public required X509Certificate Certificate { get; init; }
    public required RsaPrivateCrtKeyParameters PrivateKey { get; init; }

    public static LegacyTlsCredentials LoadOrCreate(string directory, string commonName, int keyBits = 2048)
    {
        string keyPath = Path.Combine(directory, "tls-key.pem");
        string certPath = Path.Combine(directory, "tls-cert.pem");
        if (File.Exists(keyPath) && File.Exists(certPath))
        {
            using TextReader keyText = File.OpenText(keyPath);
            using TextReader certText = File.OpenText(certPath);
            object key = new PemReader(keyText).ReadObject();
            RsaPrivateCrtKeyParameters privateKey = key switch
            {
                AsymmetricCipherKeyPair pair => (RsaPrivateCrtKeyParameters)pair.Private,
                RsaPrivateCrtKeyParameters single => single,
                _ => throw new InvalidDataException($"{keyPath} does not hold an RSA private key"),
            };
            return new() { Certificate = (X509Certificate)new PemReader(certText).ReadObject(), PrivateKey = privateKey };
        }

        LegacyTlsCredentials created = Create(commonName, keyBits);
        Directory.CreateDirectory(directory);
        using (StreamWriter keyOut = File.CreateText(keyPath))
            new PemWriter(keyOut).WriteObject(created.PrivateKey);
        using (StreamWriter certOut = File.CreateText(certPath))
            new PemWriter(certOut).WriteObject(created.Certificate);
        return created;
    }

    public static LegacyTlsCredentials Create(string commonName, int keyBits = 2048)
    {
        SecureRandom random = new();
        RsaKeyPairGenerator generator = new();
        generator.Init(new RsaKeyGenerationParameters(BigInteger.ValueOf(65537), random, keyBits, 80));
        AsymmetricCipherKeyPair pair = generator.GenerateKeyPair();

        X509Name name = new($"CN={commonName}");
        X509V3CertificateGenerator certificate = new();
        certificate.SetSerialNumber(BigInteger.ProbablePrime(64, random));
        certificate.SetIssuerDN(name);
        certificate.SetSubjectDN(name);
        certificate.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        certificate.SetNotAfter(DateTime.UtcNow.AddYears(20));
        certificate.SetPublicKey(pair.Public);
        return new()
        {
            Certificate = certificate.Generate(new Asn1SignatureFactory("SHA1WITHRSA", pair.Private, random)),
            PrivateKey = (RsaPrivateCrtKeyParameters)pair.Private,
        };
    }
}

/// <summary>A TLS or SSL server connection the WEB listener feeds bytes through.</summary>
public interface IWebTlsSession
{
    bool HandshakeComplete { get; }
    /// <summary>The version and suite agreed on, once the handshake is done; for logs.</summary>
    string Negotiated { get; }
    bool IsClosed { get; }
    /// <summary>Feeds bytes read from the network; returns the application data they completed.</summary>
    byte[] Receive(ReadOnlySpan<byte> data);
    /// <summary>Encrypts application data; <see cref="TakeOutput"/> then has the records.</summary>
    void Send(ReadOnlySpan<byte> data);
    /// <summary>Everything the protocol wants on the wire now.</summary>
    byte[] TakeOutput();
}

/// <summary>
/// One TLS server connection, run without a socket: bytes from the network go
/// in through <see cref="Receive"/>, what to send back and the decrypted
/// application data come out. BouncyCastle does the protocol, so SSL 3.0-era
/// clients (TLS 1.0, RSA key exchange, CBC-SHA suites) are served whatever the
/// host's OpenSSL policy allows.
/// </summary>
public sealed class LegacyTlsSession : IWebTlsSession
{
    private readonly TlsServerProtocol _protocol = new();
    private readonly LegacyTlsServer _server;

    public LegacyTlsSession(LegacyTlsCredentials credentials)
    {
        _server = new LegacyTlsServer(credentials);
        _protocol.Accept(_server);
    }

    public bool HandshakeComplete => _server.HandshakeComplete;

    /// <summary>The version and suite agreed on, once the handshake is done; for logs.</summary>
    public string Negotiated => _server.Negotiated;

    public bool IsClosed => _protocol.IsClosed;

    /// <summary>
    /// Feeds bytes read from the network. Returns the decrypted application data
    /// they completed (often none); <see cref="TakeOutput"/> then has what to send.
    /// </summary>
    public byte[] Receive(ReadOnlySpan<byte> data)
    {
        _protocol.OfferInput(data.ToArray());
        int available = _protocol.GetAvailableInputBytes();
        if (available == 0)
            return [];
        byte[] plain = new byte[available];
        _protocol.ReadInput(plain, 0, available);
        return plain;
    }

    /// <summary>Encrypts application data; <see cref="TakeOutput"/> then has the records.</summary>
    public void Send(ReadOnlySpan<byte> data)
    {
        byte[] copy = data.ToArray();
        _protocol.WriteApplicationData(copy, 0, copy.Length);
    }

    public void Close() => _protocol.Close();

    /// <summary>Everything the protocol wants on the wire now.</summary>
    public byte[] TakeOutput()
    {
        int length = _protocol.GetAvailableOutputBytes();
        if (length == 0)
            return [];
        byte[] output = new byte[length];
        _protocol.ReadOutput(output, 0, length);
        return output;
    }

    private sealed class LegacyTlsServer(LegacyTlsCredentials credentials) : DefaultTlsServer(new BcTlsCrypto(new SecureRandom()))
    {
        public bool HandshakeComplete { get; private set; }
        public string Negotiated { get; private set; } = "";

        /// <summary>SSL 3.0-era clients first: TLS 1.0 up to 1.2.</summary>
        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv12.DownTo(ProtocolVersion.TLSv10);

        /// <summary>
        /// RSA key exchange with CBC-SHA suites, the ones a 2008 PS3 SSL library offers
        /// [guess: cellSsl's suite list is not read out yet], then BouncyCastle's defaults.
        /// </summary>
        protected override int[] GetSupportedCipherSuites() =>
        [
            CipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
            CipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA,
            CipherSuite.TLS_RSA_WITH_3DES_EDE_CBC_SHA,
            .. base.GetSupportedCipherSuites(),
        ];

        protected override TlsCredentialedDecryptor GetRsaEncryptionCredentials() =>
            new BcDefaultTlsCredentialedDecryptor((BcTlsCrypto)Crypto, Chain(), credentials.PrivateKey);

        protected override TlsCredentialedSigner GetRsaSignerCredentials() =>
            new BcDefaultTlsCredentialedSigner(new TlsCryptoParameters(m_context), (BcTlsCrypto)Crypto, credentials.PrivateKey, Chain(),
                TlsUtilities.IsTlsV12(m_context) ? SignatureAndHashAlgorithm.GetInstance(HashAlgorithm.sha1, SignatureAlgorithm.rsa) : null);

        private Certificate Chain() =>
            new([new BcTlsCertificate((BcTlsCrypto)Crypto, credentials.Certificate.CertificateStructure)]);

        public override void NotifyHandshakeComplete()
        {
            base.NotifyHandshakeComplete();
            HandshakeComplete = true;
            Negotiated = $"{m_context.ServerVersion}, cipher suite 0x{m_context.SecurityParameters.CipherSuite:X4}";
        }
    }
}
