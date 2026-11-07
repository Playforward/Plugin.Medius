using System.Collections.Concurrent;
using System.Net;

namespace Plugin.Medius.Web;

/// <summary>
/// The WEB listener: HTTPS (or plain HTTP) for the title extensions that run a
/// web service beside Medius (<see cref="IMediusWebService"/>). TLS is ended
/// here with <see cref="LegacyTlsSession"/>, so the PS3's TLS 1.0 clients are
/// served on hosts whose OpenSSL refuses them; a connection whose first byte
/// is not a TLS handshake record (0x16) is taken as plain HTTP. Each request
/// goes to the service whose <see cref="IMediusWebService.WebHosts"/> has its
/// Host, or to the only service when there is one.
/// </summary>
/// <remarks>
/// Not bound to a socket: the plugin hands it each connection's bytes and a
/// way to send. ServerApp's TCP listener passes raw bytes on for anything not
/// starting with an upper-case ASCII letter, which a TLS record never does;
/// plain HTTP on the same port goes to ServerApp's HTTP routes instead.
/// </remarks>
public sealed class WebComponent(IReadOnlyList<IMediusWebService> services, LegacyTlsCredentials credentials)
{
    private const byte TlsHandshakeRecord = 0x16;
    private readonly ConcurrentDictionary<Guid, WebConnection> _connections = new();

    public IReadOnlyList<IMediusWebService> Services => services;

    /// <summary>Bytes arrived on a WEB connection; <paramref name="send"/> carries whatever goes back.</summary>
    public void Received(Guid connectionId, IPEndPoint? remote, ReadOnlySpan<byte> data, Action<byte[]> send, Action disconnect)
    {
        bool isNew = !_connections.ContainsKey(connectionId);
        WebConnection connection = isNew ? _connections.GetOrAdd(connectionId, new WebConnection(OpenSession(data), remote)) : _connections[connectionId];
        if (isNew)
            Log.Debug("[medius/web] {Remote}: new connection ({Kind}), first {Length} bytes {Bytes}", remote,
                connection.Tls?.GetType().Name ?? "plain HTTP", data.Length, Convert.ToHexString(data[..Math.Min(data.Length, 96)]));
        try
        {
            byte[] plain = data.ToArray();
            if (connection.Tls is { } tls)
            {
                bool wasDone = tls.HandshakeComplete;
                plain = tls.Receive(data);
                Flush(tls, send);
                if (!wasDone && tls.HandshakeComplete)
                    Log.Debug("[medius/web] {Remote}: TLS up ({Negotiated})", remote, tls.Negotiated);
                if (tls.IsClosed)
                {
                    if (tls is Ssl3Session { LastAlert: { } alert })
                        Log.Debug("[medius/web] {Remote}: client sent SSL alert level {Level} description {Description}", remote, alert.Level, alert.Description);
                    Disconnected(connectionId);
                    disconnect();
                    return;
                }
            }
            if (plain.Length == 0)
                return;

            foreach (MediusWebRequest request in connection.Reader.Feed(plain, remote))
            {
                MediusWebResponse response = Handle(request);
                bool keepAlive = !string.Equals(request.Headers.GetValueOrDefault("connection"), "close", StringComparison.OrdinalIgnoreCase);
                byte[] bytes = response.Serialize(keepAlive);
                if (connection.Tls is { } session)
                {
                    session.Send(bytes);
                    Flush(session, send);
                }
                else
                    send(bytes);
            }
        }
        catch (Exception exception)
        {
            Log.Warning(exception, "[medius/web] {Remote}: connection dropped", remote);
            if (connection.Tls is { } session)
                Flush(session, send);
            Disconnected(connectionId);
            disconnect();
        }
    }

    /// <summary>
    /// SSL 3.0 for an SSLv2-format hello or an SSL 3.0 record (Sony's rt_ssl,
    /// <see cref="Ssl3Session"/>), BouncyCastle TLS for other handshake records,
    /// none (plain HTTP) otherwise.
    /// </summary>
    private IWebTlsSession? OpenSession(ReadOnlySpan<byte> first) =>
        Ssl3Session.IsHello(first) ? new Ssl3Session(credentials)
        : first.Length > 0 && first[0] == TlsHandshakeRecord ? new LegacyTlsSession(credentials)
        : null;

    public void Disconnected(Guid connectionId)
    {
        if (_connections.TryRemove(connectionId, out WebConnection? connection) && connection.Tls is { HandshakeComplete: false })
            Log.Debug("[medius/web] {Remote}: closed before the TLS handshake finished", connection.Remote);
    }

    /// <summary>Answers one request: the service whose host it is (or that claims its path), else 404.</summary>
    public MediusWebResponse Handle(MediusWebRequest request) => TryHandle(request) ?? Answer(null, request);

    /// <summary>
    /// Plain HTTP from ServerApp's routes (<see cref="MediusHttpRoutes"/>): the answer of the
    /// service whose host it is (or that claims its path), or null when none does, so other routes can try.
    /// </summary>
    public MediusWebResponse? TryHandle(MediusWebRequest request)
    {
        // A URL on the server's own address (MAG's launcher patches and SVO) names no game
        // host: the services without hosts of their own are asked first, and the first
        // answer is taken. They answer only their own paths, so a title whose host is the
        // server's address (Killzone 2's SVO) still gets the rest.
        foreach (IMediusWebService hostless in services.Where(s => s.WebHosts.Count == 0))
            if (Answer(hostless, request, quietWhenUnclaimed: true) is { } response)
                return response;
        return ServiceFor(request) is { } service ? Answer(service, request) : null;
    }

    /// <summary>The service whose host the request names; else the only service.</summary>
    private IMediusWebService? ServiceFor(MediusWebRequest request) =>
        services.FirstOrDefault(s => s.WebHosts.Any(h => string.Equals(h, request.Host, StringComparison.OrdinalIgnoreCase)))
        ?? (services.Count == 1 ? services[0] : null);

    private static MediusWebResponse Answer(IMediusWebService? service, MediusWebRequest request) => Answer(service, request, quietWhenUnclaimed: false)!;

    /// <summary>The service's answer, logged; null only when <paramref name="quietWhenUnclaimed"/> and the service has nothing at the path.</summary>
    private static MediusWebResponse? Answer(IMediusWebService? service, MediusWebRequest request, bool quietWhenUnclaimed)
    {
        MediusWebResponse response;
        try
        {
            MediusWebResponse? answer = service?.HandleWeb(request);
            if (answer is null && quietWhenUnclaimed)
                return null;
            response = answer ?? MediusWebResponse.NotFound();
        }
        catch (Exception exception)
        {
            Log.Error(exception, "[medius/web] {Request} failed", request);
            response = MediusWebResponse.Text("server error", 500);
        }
        Log.Information("[medius/web] {Remote} {Request} ({Length} body bytes) -> {Status} ({ResponseLength} bytes)",
            request.Remote, request, request.Body.Length, response.Status, response.Body.Length);
        return response;
    }

    private static void Flush(IWebTlsSession tls, Action<byte[]> send)
    {
        byte[] output = tls.TakeOutput();
        if (output.Length > 0)
            send(output);
    }

    private sealed class WebConnection(IWebTlsSession? tls, IPEndPoint? remote)
    {
        public IWebTlsSession? Tls { get; } = tls;
        public IPEndPoint? Remote { get; } = remote;
        public HttpRequestReader Reader { get; } = new();
    }
}
