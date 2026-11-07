using ModdableWebServer.Helper;
using Plugin.Medius.Extensions.Killzone2;
using Plugin.Medius.Web;
using ServerShared.Server;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Plugin.Medius.Tests;

/// <summary>
/// Plain HTTP on a WEB listener: ServerApp's own HTTP server takes the connection,
/// <see cref="MediusHttpRoutes"/> hands it to the title's web service. Played with the
/// request Killzone 2 makes to an http SvoUrl.
/// </summary>
public sealed class MediusHttpRoutesTests
{
    [Fact]
    public async Task Killzone2_root_request_over_plain_http_gets_the_uri_list_and_mac()
    {
        int port;
        using (Socket probe = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }
        Killzone2Extension extension = new();
        extension.Initialize(new MediusTitle
        {
            ExtensionSettings = JsonDocument.Parse("""
                { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test", "ResponseMacKey": "sp9ck0348sld00000000000000000000",
                           "RootPath": "/killzone2/index", "Uris": [ { "Name": "homeURI", "Value": "/killzone2/home" } ] } }
                """).RootElement,
        }, null!);
        CoreUnsecureServer server = new(port);
        server.HeaderAttributeToMethods.Merge(typeof(MediusHttpRoutes));
        MediusHttpRoutes.Web = new WebComponent([extension], LegacyTlsCredentials.Create("svo.test", 1024));
        Assert.True(server.Start());
        try
        {
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, port);
            NetworkStream stream = client.GetStream();
            // As Killzone 2 1.29 sends it (first SVO request, 2026-10-30), at PS Rewired's root path.
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                "GET /killzone2/index?languageID=1 HTTP/1.0\r\nHost: svo.test\r\nConnection: close\r\n" +
                "X-SVOMac: 04d1fba9e47ac792eade8bf111ecf6cc\r\nSVO-Platform: PS3\r\nContent-Length: 0\r\n\r\n"));
            StringBuilder received = new();
            byte[] buffer = new byte[8192];
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            while (!received.ToString().Contains("</XML>"))
            {
                int read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                    break;
                received.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
            string answer = received.ToString();
            Assert.StartsWith("HTTP/1.1 200", answer);
            Assert.Contains("X-SVOMac: 8278b53fbf31d1bc2ba304cbb7ba61ab", answer);
            Assert.Contains("<DATA dataType=\"URI\" name=\"homeURI\" value=\"http://svo.test/killzone2/home\"/>", answer);
        }
        finally
        {
            server.Stop();
            MediusHttpRoutes.Web = null;
        }
    }
}
