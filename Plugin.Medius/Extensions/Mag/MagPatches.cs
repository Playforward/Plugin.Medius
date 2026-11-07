using Plugin.Medius.Web;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// The launcher's DLS data patches (2.x), served as retail did: the lobby
/// answers the launcher's PatchRequest with a PatchURL per patch it lacks,
/// and the launcher's DLS (Sony's libpace, bin/libpace_g.sprx, which the
/// game ships and RPCS3 runs as is) fetches each over HTTP from the web
/// listener. A patch is <c>patch_r&lt;requires&gt;_&lt;provides&gt;.psarc</c>:
/// the build it applies to and the build it makes, as its /patch.xml's
/// &lt;requires&gt; and &lt;provides&gt; say (r000000_445230 → r445230_465304 →
/// r465304_467225 → r467225_489979 for 2.12). The launcher reports the
/// builds its installed patches provide (PatchRequest's levels), so the
/// patches it needs are the chain from what it has.
/// </summary>
public sealed class MagPatches
{
    private static readonly Regex PatchName = new(@"^patch_r(\d+)_(\d+)\.psarc$", RegexOptions.IgnoreCase);

    /// <summary>One patch archive on disk.</summary>
    public sealed record PatchFile(string Name, string Path, int Requires, int Provides, long Size);

    public IReadOnlyList<PatchFile> Files { get; }

    /// <summary>The URL path the files are under (e.g. /mag/patches).</summary>
    public string UrlPath { get; }

    public MagPatches(string directory, string urlPath)
    {
        UrlPath = "/" + urlPath.Trim('/');
        Files = Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory)
                .Select(path => (path, match: PatchName.Match(System.IO.Path.GetFileName(path))))
                .Where(f => f.match.Success)
                .Select(f => new PatchFile(System.IO.Path.GetFileName(f.path), f.path,
                    int.Parse(f.match.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(f.match.Groups[2].Value, CultureInfo.InvariantCulture),
                    SizeOf(f.path)))
                .OrderBy(f => f.Requires)]
            : [];
    }

    /// <summary>A file's size, through a symbolic link to the archive if it is one.</summary>
    private static long SizeOf(string path)
    {
        FileInfo info = new(path);
        return info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target ? target.Length : info.Length;
    }

    /// <summary>
    /// The patches a launcher with <paramref name="installed"/> builds needs, in
    /// the order they apply: each one requires the base (0) or a build it has
    /// or will have, and provides one it hasn't.
    /// </summary>
    public IReadOnlyList<PatchFile> Needed(IEnumerable<int> installed)
    {
        HashSet<int> have = [0, .. installed];
        List<PatchFile> needed = [];
        while (Files.FirstOrDefault(f => have.Contains(f.Requires) && !have.Contains(f.Provides)) is { } next)
        {
            needed.Add(next);
            have.Add(next.Provides);
        }
        return needed;
    }

    /// <summary>
    /// A file under <see cref="UrlPath"/>, whole or the byte range asked for
    /// (Range: bytes=a-b, a-, -n), read from disk per request; null for any
    /// other path.
    /// </summary>
    public MediusWebResponse? Serve(MediusWebRequest request)
    {
        string prefix = UrlPath + "/";
        if (!request.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string name = Uri.UnescapeDataString(request.Path[prefix.Length..]);
        if (Files.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } file)
            return MediusWebResponse.NotFound();

        long start = 0, end = file.Size - 1;
        bool partial = false;
        if (request.Headers.GetValueOrDefault("range") is { } range && ParseRange(range, file.Size) is { } r)
            (start, end, partial) = (r.Start, r.End, true);
        else if (request.Headers.ContainsKey("range"))
            return new MediusWebResponse
            {
                Status = 416,
                Headers = new Dictionary<string, string> { ["Content-Range"] = $"bytes */{file.Size}" },
            };

        long length = end - start + 1;
        if (length > Array.MaxLength)
            return MediusWebResponse.Text($"ask for {file.Name} in ranges", 413);
        byte[] body = new byte[length];
        using (FileStream stream = new(file.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(body);
        }
        Dictionary<string, string> headers = new() { ["Accept-Ranges"] = "bytes" };
        if (partial)
            headers["Content-Range"] = $"bytes {start}-{end}/{file.Size}";
        return new MediusWebResponse { Status = partial ? 206 : 200, ContentType = "application/octet-stream", Body = body, Headers = headers };
    }

    /// <summary>The first range of a "bytes=" Range header, clamped to the file; null if it can't be satisfied.</summary>
    public static (long Start, long End)? ParseRange(string header, long size)
    {
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || size == 0)
            return null;
        string first = header[6..].Split(',')[0].Trim();
        int dash = first.IndexOf('-');
        if (dash < 0)
            return null;
        string a = first[..dash].Trim(), b = first[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix == 0)
                return null;
            return (Math.Max(0, size - suffix), size - 1);
        }
        if (!long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out long start) || start >= size)
            return null;
        long end = b.Length == 0 ? size - 1
            : long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out long e) && e >= start ? Math.Min(e, size - 1)
            : -1;
        return end < 0 ? null : (start, end);
    }
}
