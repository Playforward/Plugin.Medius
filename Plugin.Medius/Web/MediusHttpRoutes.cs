using ModdableWebServer.Attributes;
using ModdableWebServer.Senders;
using System.Net;

namespace Plugin.Medius.Web;

/// <summary>
/// Plain HTTP on a WEB listener. ServerApp hands a connection whose first byte
/// is an upper-case letter to its own HTTP routes rather than to the plugin, so
/// these routes take any path (up to eight segments, with or without a query)
/// and pass the request to <see cref="WebComponent"/>, like a request that came
/// over TLS. Killzone 2's SVO is plain HTTP (PS Rewired serves it so [live]).
/// </summary>
/// <remarks>
/// Header routes (any Host) rather than plain HTTP routes: another plugin's
/// catch-alls with the same patterns (GameSpy's) would collide as dictionary
/// keys. Merged only onto WEB listeners (<see cref="MediusPlugin"/>), and a
/// request no title's web service claims is declined, so other routes still run.
/// </remarks>
internal static class MediusHttpRoutes
{
    /// <summary>The plugin's web component; set while the plugin runs.</summary>
    public static WebComponent? Web { get; set; }

    private const string Host = "host";

    [HTTPHeader("GET", "/", Host)]
    [HTTPHeader("POST", "/", Host)]
    [HTTPHeader("GET", "/?{!args}", Host)]
    [HTTPHeader("POST", "/?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}/{p2}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}/{p2}/{p3}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}/{p2}/{p3}/{p4}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}/{p5}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}/{p2}/{p3}/{p4}/{p5}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}/{p7}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}/{p7}?{!args}", Host)]
    [HTTPHeader("GET", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}/{p7}/{p8}?{!args}", Host)]
    [HTTPHeader("POST", "/{p1}/{p2}/{p3}/{p4}/{p5}/{p6}/{p7}/{p8}?{!args}", Host)]
    public static bool Call(ServerSender sender)
    {
        if (Web is not { } web)
            return false;
        MediusWebRequest request = new()
        {
            Method = sender.Request.Method,
            Target = sender.Request.Url,
            Protocol = sender.Request.Protocol,
            Headers = new Dictionary<string, string>(sender.Headers),
            Body = sender.Request.BodyBytes,
            Remote = (sender.Session as NetCoreServer.TcpSession)?.Socket?.RemoteEndPoint as IPEndPoint,
        };
        if (web.TryHandle(request) is not { } response)
            return false;

        // Sony's SVO client sends HTTP/1.0 with "Connection: Close" and finishes a download only when the
        // server closes: Resistance: Fall of Man's patch manifest never completed on a kept-alive connection,
        // and the game gave up ("patch file download failed") [live 2026-11-01]. PSRewired answers
        // "Connection: close" and closes [live].
        string? connection = request.Headers.GetValueOrDefault("connection");
        bool close = string.Equals(connection, "close", StringComparison.OrdinalIgnoreCase) ||
                     (request.Protocol == "HTTP/1.0" && !string.Equals(connection, "keep-alive", StringComparison.OrdinalIgnoreCase));

        sender.Response.Clear().SetBegin(response.Status).SetHeader("Content-Type", response.ContentType);
        foreach ((string name, string value) in response.Headers)
            sender.Response.SetHeader(name, value);
        foreach (string cookie in response.SetCookies)
            sender.Response.SetHeader("Set-Cookie", cookie);
        if (close)
            sender.Response.SetHeader("Connection", "close");
        sender.Response.SetBody(response.Body);
        sender.SendResponse();
        if (close)
            (sender.Session as NetCoreServer.TcpSession)?.Disconnect();
        return true;
    }
}
