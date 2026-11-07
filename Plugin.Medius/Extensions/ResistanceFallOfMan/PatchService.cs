using System.Buffers.Binary;
using System.Text;

namespace Plugin.Medius.Extensions.ResistanceFallOfMan;

/// <summary>
/// Resistance: Fall of Man's in-game patch, as the disc build fetches it (disc EBOOT 1.00) [C]:
/// <list type="bullet">
/// <item>The universe's ExtendedInfo is "&lt;version&gt; &lt;URL&gt;" (sscanf "%s %s", universe callback
/// 0x1ba9cc); 0x2fec3c reads the version with "%d.%d" as executable and data version. It queues
/// EBOOT.BIN (type 1) when its own executable version is lower, and patch_NN.psarc (type 2, N = 1..data
/// version) for each data patch it has no info file for; names from 0x2fe780.</item>
/// <item>It then downloads URL/manifest.dat and each queued file (URL "%s/%s", 0x2fee74) through the SVO
/// HTTP client, so only while SVO is connected. PARAM.SFO (type 3) is queued only when the manifest
/// lists it.</item>
/// <item>manifest.dat, big-endian: u16 0x4947 ("IG"), u16 1 (checked at 0x3005fc), u32 count, then per
/// file u64 size, u32 CRC and a 20-byte name (lookup 0x2fde58, 0x20-byte entries; the patch state
/// allocates 0xca8 bytes, room for 100).</item>
/// <item>The CRC is CRC-32's table with seed 0xFFFFFFFF and no final XOR (0xe2880): the bitwise NOT of
/// the usual CRC-32. It matches the info files the game kept for PSRewired's patch.</item>
/// </list>
/// The files are served from a directory; the manifest is built from it at start, in the configured
/// order.
/// </summary>
public sealed class PatchService
{
    public const ushort Magic = 0x4947;
    public const ushort ManifestVersion = 1;
    public const int NameLength = 20;
    public const string ManifestName = "manifest.dat";

    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>The request path the files are under (the ExtendedInfo URL's path).</summary>
    public string Path { get; }

    public byte[] Manifest { get; }

    public PatchService(string path, string directory, IEnumerable<string> files)
    {
        Path = "/" + path.Trim('/');
        List<(string Name, long Size, uint Crc)> entries = [];
        foreach (string name in files)
        {
            if (Encoding.ASCII.GetByteCount(name) >= NameLength || name.Contains('/') || name.Contains('\\'))
                throw new ArgumentException($"patch file name '{name}' must be a plain name under {NameLength} bytes");
            string file = System.IO.Path.Combine(directory, name);
            using FileStream stream = File.OpenRead(file);
            entries.Add((name, stream.Length, Crc(stream)));
            _files[name] = file;
        }
        Manifest = BuildManifest(entries);
    }

    // The client's table (disc 0x1000a4a0: 0, 0x77073096, 0xee0e612c, ...), CRC-32's reflected 0xEDB88320 [C].
    private static readonly uint[] Table = [.. Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++)
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    })];

    /// <summary>The game's CRC (0xe2880): crc = (crc &gt;&gt; 8) ^ table[(crc ^ byte) &amp; 0xff], seeded with 0xFFFFFFFF, no final XOR.</summary>
    public static uint Crc(Stream stream)
    {
        uint crc = 0xFFFFFFFF;
        byte[] buffer = new byte[1 << 16];
        int read;
        while ((read = stream.Read(buffer)) > 0)
            foreach (byte b in buffer.AsSpan(0, read))
                crc = (crc >> 8) ^ Table[(crc ^ b) & 0xff];
        return crc;
    }

    public static byte[] BuildManifest(IReadOnlyList<(string Name, long Size, uint Crc)> entries)
    {
        byte[] manifest = new byte[8 + 0x20 * entries.Count];
        BinaryPrimitives.WriteUInt16BigEndian(manifest, Magic);
        BinaryPrimitives.WriteUInt16BigEndian(manifest.AsSpan(2), ManifestVersion);
        BinaryPrimitives.WriteUInt32BigEndian(manifest.AsSpan(4), (uint)entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            Span<byte> entry = manifest.AsSpan(8 + 0x20 * i, 0x20);
            BinaryPrimitives.WriteUInt64BigEndian(entry, (ulong)entries[i].Size);
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], entries[i].Crc);
            Encoding.ASCII.GetBytes(entries[i].Name, entry[12..]);
        }
        return manifest;
    }

    /// <summary>The manifest or a listed file for a request under <see cref="Path"/>; null for anything else.</summary>
    public byte[]? Find(string requestPath)
    {
        if (!requestPath.StartsWith(Path + "/", StringComparison.OrdinalIgnoreCase))
            return null;
        string name = requestPath[(Path.Length + 1)..];
        if (name == ManifestName)
            return Manifest;
        return _files.TryGetValue(name, out string? file) ? File.ReadAllBytes(file) : null;
    }
}
