using System.Security.Cryptography;
using System.Text;

namespace Plugin.Medius.Extensions.Killzone2;

/// <summary>
/// The X-SVOMac header of Sony's SVO HTTP client (svoclient 3.03, Killzone 2
/// 1.29). Request: the request builder 0x6ce82c calls 0x6ccb64, which MD5s
/// (0x6cab08 init, 0x6cb7bc update, 0x6cb8e8 final, 0x6cb744 hex) the request
/// path, the Cookie header's value (the line after its 8-byte "Cookie: "
/// prefix, without its CRLF; skipped when there is none), the form fields
/// (skipped when there is none) and the connection's scheme string
/// (conn+0x428, set to "http://" at 0x6cf788), and sends the lower-case hex
/// [C]. The path is the whole request target, query included, and the "body" is
/// the request's form-field string (strlen'd), not a binary body: the ticket login
/// POST /killzone2/https:ticket/login?sig=..&languageID=2 with the NP ticket as its
/// body sent MD5(target + "sig=..&languageID=2" + "http://") [live 2026-10-31].
/// GET / with no cookie or fields sent 5014b6872cafea54664b932eaafa1596 =
/// MD5("/" + "http://") [live 2026-10-30].
/// </summary>
/// <remarks>
/// Response: for a 200, 0x6ce3ac calls 0x6cca14, which MD5s the request's
/// MAC hex (kept at conn+20 by 0x6ccb48) and a constant
/// ("sp9ck0348sld00000000000000000000" at 0xc48708 on 1.29, the title's
/// <c>ResponseMacKey</c>). In 1.29 it then returns success whatever the
/// response's X-SVOMac says, so the check is off, but the server sends the
/// value it would expect [C].
/// </remarks>
public static class SvoMac
{
    /// <summary>The MAC a client sends with a request (<paramref name="body"/>: its form-field string, if any).</summary>
    public static string Request(string path, string? cookie, ReadOnlySpan<byte> body, string scheme)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(Encoding.ASCII.GetBytes(path));
        if (!string.IsNullOrEmpty(cookie))
            md5.AppendData(Encoding.ASCII.GetBytes(cookie));
        if (!body.IsEmpty)
            md5.AppendData(body);
        md5.AppendData(Encoding.ASCII.GetBytes(scheme));
        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>The MAC the client computes for the answer to a request whose MAC was <paramref name="requestMac"/>.</summary>
    public static string Response(string requestMac, string key) =>
        Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes(requestMac + key)));
}
