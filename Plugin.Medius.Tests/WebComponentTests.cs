using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Plugin.Medius.Web;
using System.Text;

namespace Plugin.Medius.Tests;

/// <summary>
/// The WEB listener's HTTPS: a TLS 1.0 client offering only RSA/AES-CBC-SHA (what a
/// 2008 PS3 SSL library is expected to offer) gets through the handshake and an HTTP
/// answer, and requests are reassembled however the bytes arrive.
/// </summary>
public sealed class WebComponentTests
{
    private static readonly LegacyTlsCredentials Credentials = LegacyTlsCredentials.Create("test.example", 1024);

    private sealed class EchoService : IMediusWebService
    {
        public IReadOnlyCollection<string> WebHosts => ["stats.example"];

        public MediusWebResponse? HandleWeb(MediusWebRequest request) =>
            request.Path == "/echo" ? MediusWebResponse.Text($"{request.Method} {request.QueryString("a")} {Encoding.ASCII.GetString(request.Body)}") : null;
    }

    private sealed class OldClient(ProtocolVersion version, int suite) : DefaultTlsClient(new BcTlsCrypto(new SecureRandom()))
    {
        protected override ProtocolVersion[] GetSupportedVersions() => [version];
        protected override int[] GetSupportedCipherSuites() => [suite];
        public override TlsAuthentication GetAuthentication() => new AcceptAny();

        private sealed class AcceptAny : TlsAuthentication
        {
            public void NotifyServerCertificate(TlsServerCertificate serverCertificate) { }
            public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
        }
    }

    private static string RoundTrip(ProtocolVersion version, int suite, string request)
    {
        WebComponent web = new([new EchoService()], Credentials);
        Guid id = Guid.NewGuid();
        TlsClientProtocol client = new();
        client.Connect(new OldClient(version, suite));
        List<byte> toClient = [];

        void Pump()
        {
            for (int round = 0; round < 20; round++)
            {
                int pending = client.GetAvailableOutputBytes();
                if (pending == 0 && toClient.Count == 0)
                    return;
                if (pending > 0)
                {
                    byte[] output = new byte[pending];
                    client.ReadOutput(output, 0, pending);
                    web.Received(id, null, output, toClient.AddRange, () => { });
                }
                if (toClient.Count > 0)
                {
                    client.OfferInput([.. toClient]);
                    toClient.Clear();
                }
            }
        }

        Pump();
        byte[] bytes = Encoding.ASCII.GetBytes(request);
        client.WriteApplicationData(bytes, 0, bytes.Length);
        Pump();
        int available = client.GetAvailableInputBytes();
        byte[] answer = new byte[available];
        client.ReadInput(answer, 0, available);
        return Encoding.ASCII.GetString(answer);
    }

    [Fact]
    public void Tls10_rsa_aes_client_gets_an_answer()
    {
        string answer = RoundTrip(ProtocolVersion.TLSv10, CipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
            "POST /echo?a=1 HTTP/1.1\r\nHost: stats.example:10091\r\nContent-Length: 4\r\n\r\nbody");
        Assert.StartsWith("HTTP/1.1 200 OK\r\n", answer);
        Assert.EndsWith("\r\n\r\nPOST 1 body", answer);
    }

    [Fact]
    public void Tls10_3des_and_tls12_clients_get_answers()
    {
        Assert.EndsWith("GET 2 ", RoundTrip(ProtocolVersion.TLSv10, CipherSuite.TLS_RSA_WITH_3DES_EDE_CBC_SHA,
            "GET /echo?a=2 HTTP/1.1\r\nHost: stats.example\r\n\r\n"));
        Assert.EndsWith("GET 3 ", RoundTrip(ProtocolVersion.TLSv12, CipherSuite.TLS_RSA_WITH_AES_256_CBC_SHA,
            "GET /echo?a=3 HTTP/1.1\r\nHost: stats.example\r\n\r\n"));
    }

    [Fact]
    public void Unknown_path_is_404()
    {
        Assert.StartsWith("HTTP/1.1 404", RoundTrip(ProtocolVersion.TLSv10, CipherSuite.TLS_RSA_WITH_AES_128_CBC_SHA,
            "GET /nothing HTTP/1.1\r\nHost: stats.example\r\n\r\n"));
    }

    [Fact]
    public void Reader_reassembles_split_and_pipelined_requests()
    {
        HttpRequestReader reader = new();
        byte[] two = Encoding.ASCII.GetBytes(
            "POST /a?x=%41+b HTTP/1.1\r\nHost: h\r\nContent-Length: 3\r\n\r\nabc" +
            "POST /b HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nde\r\n1\r\nf\r\n0\r\n\r\n");
        List<MediusWebRequest> got = [];
        foreach (byte b in two)
            got.AddRange(reader.Feed([b], null));
        Assert.Equal(2, got.Count);
        Assert.Equal("A b", got[0].QueryString("x"));
        Assert.Equal("abc", Encoding.ASCII.GetString(got[0].Body));
        Assert.Equal("/b", got[1].Path);
        Assert.Equal("def", Encoding.ASCII.GetString(got[1].Body));
    }

    [Fact]
    public void Credentials_are_kept_across_starts()
    {
        string dir = Path.Combine(Path.GetTempPath(), "medius-web-" + Guid.NewGuid().ToString("N"));
        try
        {
            LegacyTlsCredentials first = LegacyTlsCredentials.LoadOrCreate(dir, "x", 1024);
            LegacyTlsCredentials second = LegacyTlsCredentials.LoadOrCreate(dir, "x", 1024);
            Assert.Equal(first.Certificate, second.Certificate);
            Assert.Equal(first.PrivateKey.Modulus, second.PrivateKey.Modulus);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
