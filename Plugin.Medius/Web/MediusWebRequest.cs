using System.Globalization;
using System.Net;
using System.Text;

namespace Plugin.Medius.Web;

/// <summary>One HTTP request a game's client made to a title's web service.</summary>
public sealed class MediusWebRequest
{
    public string Method { get; init; } = "GET";
    /// <summary>The request target as sent, query included.</summary>
    public string Target { get; init; } = "/";
    public string Protocol { get; init; } = "HTTP/1.1";
    /// <summary>Header names lower-cased; a repeated header keeps its last value.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public byte[] Body { get; init; } = [];
    public IPEndPoint? Remote { get; init; }

    /// <summary>The path without the query.</summary>
    public string Path => Target.Split('?', 2)[0];

    /// <summary>The Host header without a port, lower-cased; "" when there was none.</summary>
    public string Host => (Headers.GetValueOrDefault("host") ?? "").Split(':')[0].Trim().ToLowerInvariant();

    private Dictionary<string, string>? _query;

    /// <summary>Query fields, percent-decoded; a repeated name keeps its last value.</summary>
    public IReadOnlyDictionary<string, string> Query => _query ??= ParseQuery(Target.Contains('?') ? Target.Split('?', 2)[1] : "");

    public string? QueryString(string name) => Query.GetValueOrDefault(name);

    public long? QueryInt64(string name) =>
        long.TryParse(QueryString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;

    public ulong? QueryUInt64(string name) =>
        ulong.TryParse(QueryString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value) ? value : null;

    public static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> fields = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            fields[name] = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return fields;
    }

    public override string ToString() => $"{Method} {Host}{Target}";
}

/// <summary>What a title's web service answers.</summary>
public sealed class MediusWebResponse
{
    public int Status { get; init; } = 200;
    public string ContentType { get; init; } = "text/plain";
    public byte[] Body { get; init; } = [];
    /// <summary>Headers beyond Content-Type, Content-Length and Connection (e.g. Killzone 2's X-SVOMac).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    /// <summary>Set-Cookie values, one header each.</summary>
    public IReadOnlyList<string> SetCookies { get; init; } = [];

    public static MediusWebResponse Text(string text, int status = 200) =>
        new() { Status = status, ContentType = "text/plain", Body = Encoding.UTF8.GetBytes(text) };

    public static MediusWebResponse Binary(byte[] body) =>
        new() { ContentType = "application/octet-stream", Body = body };

    public static MediusWebResponse NotFound() => Text("not found", 404);

    /// <summary>The response's bytes on the wire, HTTP/1.1 with an explicit length.</summary>
    public byte[] Serialize(bool keepAlive)
    {
        string reason = Status switch { 200 => "OK", 206 => "Partial Content", 400 => "Bad Request", 404 => "Not Found", 413 => "Content Too Large", 416 => "Range Not Satisfiable", 500 => "Internal Server Error", _ => "Status" };
        string head = $"HTTP/1.1 {Status} {reason}\r\nContent-Type: {ContentType}\r\nContent-Length: {Body.Length}\r\n" +
                      string.Concat(Headers.Select(h => $"{h.Key}: {h.Value}\r\n")) +
                      string.Concat(SetCookies.Select(c => $"Set-Cookie: {c}\r\n")) +
                      $"Connection: {(keepAlive ? "keep-alive" : "close")}\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        byte[] all = new byte[headBytes.Length + Body.Length];
        headBytes.CopyTo(all, 0);
        Body.CopyTo(all, headBytes.Length);
        return all;
    }
}

/// <summary>
/// A title extension that answers HTTP(S) for its game, beside Medius (Resistance 2's
/// stats service, for one). Requests reach it through the WEB listener when their Host
/// is one of <see cref="WebHosts"/>.
/// </summary>
public interface IMediusWebService
{
    /// <summary>Host names this service answers for (the game's own, redirected here by DNS).</summary>
    IReadOnlyCollection<string> WebHosts { get; }

    /// <summary>The answer, or null when the service has nothing at that path (404).</summary>
    MediusWebResponse? HandleWeb(MediusWebRequest request);
}

/// <summary>
/// Reassembles HTTP/1.1 requests from a byte stream: request line, headers,
/// then a Content-Length or chunked body. Pipelined requests come out in order.
/// </summary>
public sealed class HttpRequestReader(int maxHeaderBytes = 32 * 1024, int maxBodyBytes = 4 * 1024 * 1024)
{
    private readonly List<byte> _buffer = [];

    public IEnumerable<MediusWebRequest> Feed(ReadOnlySpan<byte> data, IPEndPoint? remote)
    {
        _buffer.AddRange(data.ToArray());
        List<MediusWebRequest> requests = [];
        while (TryTake(remote) is { } request)
            requests.Add(request);
        return requests;
    }

    private MediusWebRequest? TryTake(IPEndPoint? remote)
    {
        byte[] bytes = [.. _buffer];
        int headerEnd = IndexOf(bytes, "\r\n\r\n"u8, 0);
        if (headerEnd < 0)
        {
            if (bytes.Length > maxHeaderBytes)
                throw new InvalidDataException($"request header over {maxHeaderBytes} bytes");
            return null;
        }

        string[] lines = Encoding.ASCII.GetString(bytes, 0, headerEnd).Split("\r\n");
        string[] requestLine = lines[0].Split(' ', 3);
        if (requestLine.Length < 2)
            throw new InvalidDataException($"bad request line '{lines[0]}'");
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
                headers[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
        }

        int bodyStart = headerEnd + 4;
        byte[] body;
        int consumed;
        if (headers.TryGetValue("transfer-encoding", out string? encoding) && encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDechunk(bytes, bodyStart, out body, out consumed))
                return null;
        }
        else
        {
            int length = headers.TryGetValue("content-length", out string? text) && int.TryParse(text, out int parsed) ? parsed : 0;
            if (length < 0 || length > maxBodyBytes)
                throw new InvalidDataException($"body of {length} bytes refused");
            if (bytes.Length - bodyStart < length)
                return null;
            body = bytes[bodyStart..(bodyStart + length)];
            consumed = bodyStart + length;
        }

        _buffer.RemoveRange(0, consumed);
        return new MediusWebRequest
        {
            Method = requestLine[0],
            Target = requestLine[1],
            Protocol = requestLine.Length > 2 ? requestLine[2] : "HTTP/1.0",
            Headers = headers,
            Body = body,
            Remote = remote,
        };
    }

    private bool TryDechunk(byte[] bytes, int start, out byte[] body, out int consumed)
    {
        List<byte> output = [];
        int p = start;
        body = [];
        consumed = 0;
        while (true)
        {
            int lineEnd = IndexOf(bytes, "\r\n"u8, p);
            if (lineEnd < 0)
                return false;
            string sizeText = Encoding.ASCII.GetString(bytes, p, lineEnd - p).Split(';')[0].Trim();
            int size = int.Parse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            p = lineEnd + 2;
            if (size == 0)
            {
                int trailerEnd = IndexOf(bytes, "\r\n"u8, p);
                if (trailerEnd < 0)
                    return false;
                body = [.. output];
                consumed = trailerEnd + 2;
                return true;
            }
            if (bytes.Length < p + size + 2)
                return false;
            output.AddRange(bytes.AsSpan(p, size).ToArray());
            if (output.Count > maxBodyBytes)
                throw new InvalidDataException($"chunked body over {maxBodyBytes} bytes");
            p += size + 2;
        }
    }

    private static int IndexOf(byte[] bytes, ReadOnlySpan<byte> pattern, int from)
    {
        int found = bytes.AsSpan(from).IndexOf(pattern);
        return found < 0 ? -1 : from + found;
    }
}
