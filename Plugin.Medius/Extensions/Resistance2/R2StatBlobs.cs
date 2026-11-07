using System.Buffers.Binary;

namespace Plugin.Medius.Extensions.Resistance2;

/// <summary>
/// A stat upload as Resistance 2 posts it to statUpload.py after its NP ticket
/// (1.60 serializers 0x2ef314 for mode 6, 0x2ef010 for mode 27 [C]):
/// <code>
/// [0]  u32 stamp     the stamp of the client's last download (player+0x60180 / +0x60184)
/// [4]  u32 id        player+0x601b4, echoed back in every download (no store to it in the EBOOT; 0 [guess])
/// [8]  u32 length    of the whole blob
/// [12] u8  count     sections that follow
/// [13] u8  types[count]
///      records[count], one per type, each a fixed-size stat record (layout per section mask, 0x2ec67c)
/// </code>
/// All big-endian. Records of one mode share a size when the mode uses one layout
/// (mode 6: mask 1 for all, 0x2ef334); mode 27's three co-op class sections use masks
/// 0x40/0x80/0x100 and differ.
/// </summary>
public sealed record R2StatUpload(uint Stamp, uint Id, byte[] Types, byte[] Records)
{
    public const int HeaderLength = 13;

    public static R2StatUpload? Parse(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderLength)
            return null;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(blob[8..]);
        int count = blob[12];
        if (length != blob.Length || HeaderLength + count > blob.Length)
            return null;
        return new R2StatUpload(BinaryPrimitives.ReadUInt32BigEndian(blob), BinaryPrimitives.ReadUInt32BigEndian(blob[4..]),
            blob.Slice(HeaderLength, count).ToArray(), blob[(HeaderLength + count)..].ToArray());
    }

    public byte[] Serialize()
    {
        byte[] blob = new byte[HeaderLength + Types.Length + Records.Length];
        BinaryPrimitives.WriteUInt32BigEndian(blob, Stamp);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), Id);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(8), (uint)blob.Length);
        blob[12] = (byte)Types.Length;
        Types.CopyTo(blob, HeaderLength);
        Records.CopyTo(blob, HeaderLength + Types.Length);
        return blob;
    }
}

/// <summary>
/// What the client reads back: a stat download (answer to statRequest type=download
/// and to statUpload with noDownload=0) and the progress bin of getPlayerStatsBin.py.
/// </summary>
public static class R2StatDownload
{
    public const int HeaderLength = 17;
    public const int ProgressHeaderLength = 16;

    /// <summary>
    /// A download (parsers 0x2f1ce8 for mode 6, 0x2ec76c for 26/27 [C]):
    /// <code>
    /// [0]  u32 stamp     kept as player+0x60180 (mode 6) / +0x60184 (27), sent back as lastSync and upload stamp
    /// [4]  u32 id        must equal player+0x601b4, else the download is ignored
    /// [8]  u32 length    must equal the response length
    /// [12] s32 wait      seconds to the next sync (0x275b48; negative = none)
    /// [16] u8  count
    /// [17] u8  types[count], then the records in the same order
    /// </code>
    /// The client checks length == 17 + count * (record size + 1) and loads each record
    /// into its section's stats.
    /// </summary>
    public static byte[] Build(uint stamp, uint id, int waitSeconds, IReadOnlyList<byte> types, ReadOnlySpan<byte> records)
    {
        byte[] blob = new byte[HeaderLength + types.Count + records.Length];
        BinaryPrimitives.WriteUInt32BigEndian(blob, stamp);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(4), id);
        BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(8), (uint)blob.Length);
        BinaryPrimitives.WriteInt32BigEndian(blob.AsSpan(12), waitSeconds);
        blob[16] = (byte)types.Count;
        for (int i = 0; i < types.Count; i++)
            blob[HeaderLength + i] = types[i];
        records.CopyTo(blob.AsSpan(HeaderLength + types.Count));
        return blob;
    }

    /// <summary>
    /// The progress bin header alone (parser 0x2f1470, at login and on the profile page [C]):
    /// <code>
    /// [0]  u32 id      must equal player+0x601b4
    /// [4]  u32 length  must equal the response length; 16 means "no progress data" and the rest is skipped
    /// [8]  u32 (not read)
    /// [12] s32 wait    seconds to the next sync (0x275b48)
    /// </code>
    /// A longer bin carries summary values at [0x10..0x2b] (stat 1 for sections 0/1/2, stat 0x4d,
    /// two more words) and a u64 at [0x34] with stats from [0x3c] (0x21d734); their meaning is
    /// not worked out, so the server sends the header only.
    /// </summary>
    public static byte[] BuildProgressHeader(uint id, int waitSeconds)
    {
        byte[] bin = new byte[ProgressHeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(bin, id);
        BinaryPrimitives.WriteUInt32BigEndian(bin.AsSpan(4), ProgressHeaderLength);
        BinaryPrimitives.WriteInt32BigEndian(bin.AsSpan(12), waitSeconds);
        return bin;
    }
}

/// <summary>One stored run of sections: their types and their records back to back.</summary>
public sealed class R2StatSegment
{
    public byte[] Types { get; set; } = [];
    public byte[] Records { get; set; } = [];
}

/// <summary>What the server keeps of one player's sections in one sync mode.</summary>
public sealed class R2ModeStats
{
    /// <summary>Bumped on every stored upload; sent as the download stamp and downloadTimestamp.</summary>
    public uint Stamp { get; set; }
    public List<R2StatSegment> Segments { get; set; } = [];
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>One player's synced stats, kept per account (AccountDataStore key "r2stats").</summary>
public sealed class R2PlayerStats
{
    /// <summary>The id field of the player's last upload, echoed in downloads and the progress bin.</summary>
    public uint Id { get; set; }
    public Dictionary<int, R2ModeStats> Modes { get; set; } = [];
}

/// <summary>
/// Merges uploads into a player's stored sections. Uploads carry only the sections
/// that changed (mode 6 includes a section when its stats are dirty, 0x2ef3ec), so a
/// section's newest record replaces its old one. Records are split by type when their
/// sizes are known: from the mode's uniform layout, from an upload of one section, or
/// by subtraction when one size of an upload is unknown; sizes are kept title-wide
/// because every client of a build lays records out alike. A run whose sizes are not
/// known yet stays whole; an older run overlapping a newer one is kept in front of it,
/// so the client, loading sections in order, ends on the newest [guess: it loads in order].
/// </summary>
public static class R2StatSections
{
    /// <summary>Learns record sizes from an upload. Returns true when something new was learnt.</summary>
    public static bool Learn(IReadOnlyList<byte> types, int recordsLength, bool uniform, Dictionary<byte, int> sizes)
    {
        if (types.Count == 0)
            return false;
        bool learnt = false;
        if (uniform || types.Count == 1 || types.All(t => t == types[0]))
        {
            if (recordsLength % types.Count != 0)
                return false;
            int size = recordsLength / types.Count;
            foreach (byte type in types.Distinct())
                learnt |= sizes.TryAdd(type, size);
            return learnt;
        }
        List<byte> unknown = [.. types.Where(t => !sizes.ContainsKey(t)).Distinct()];
        if (unknown.Count == 1)
        {
            int rest = recordsLength - types.Where(t => sizes.ContainsKey(t)).Sum(t => sizes[t]);
            int count = types.Count(t => t == unknown[0]);
            if (rest > 0 && rest % count == 0)
            {
                sizes[unknown[0]] = rest / count;
                learnt = true;
            }
        }
        return learnt;
    }

    /// <summary>Splits a run into one segment per section, or null when a size is missing or does not add up.</summary>
    public static List<R2StatSegment>? Split(R2StatSegment segment, IReadOnlyDictionary<byte, int> sizes)
    {
        if (segment.Types.Any(t => !sizes.ContainsKey(t)) || segment.Types.Sum(t => sizes[t]) != segment.Records.Length)
            return null;
        List<R2StatSegment> parts = [];
        int offset = 0;
        foreach (byte type in segment.Types)
        {
            parts.Add(new R2StatSegment { Types = [type], Records = segment.Records[offset..(offset + sizes[type])] });
            offset += sizes[type];
        }
        return parts;
    }

    /// <summary>Stores an upload's sections into <paramref name="mode"/>.</summary>
    public static void Merge(R2ModeStats mode, R2StatUpload upload, bool uniform, Dictionary<byte, int> sizes)
    {
        Learn(upload.Types, upload.Records.Length, uniform, sizes);
        // What the stored runs teach once the new sizes are in (e.g. an earlier two-section run).
        bool more = true;
        while (more)
        {
            more = false;
            foreach (R2StatSegment stored in mode.Segments)
                more |= Learn(stored.Types, stored.Records.Length, uniform, sizes);
        }

        R2StatSegment incoming = new() { Types = upload.Types, Records = upload.Records };
        List<R2StatSegment> added = Split(incoming, sizes) ?? [incoming];
        HashSet<byte> replaced = [.. upload.Types];

        List<R2StatSegment> kept = [];
        foreach (R2StatSegment stored in mode.Segments)
        {
            foreach (R2StatSegment part in Split(stored, sizes) ?? [stored])
            {
                if (part.Types.All(replaced.Contains))
                    continue;
                kept.Add(part);
            }
        }
        // A section stored twice (an old run split only now, a newer copy after it) keeps the newer.
        List<R2StatSegment> merged = [.. kept, .. added];
        mode.Segments = [.. merged.Where((segment, i) => segment.Types.Length != 1 ||
            !merged.Skip(i + 1).Any(later => later.Types.Length == 1 && later.Types[0] == segment.Types[0]))];
        mode.Stamp++;
        mode.UpdatedUtc = DateTime.UtcNow;
    }

    /// <summary>All stored sections of a mode, in order, as one types list and one records run.</summary>
    public static (byte[] Types, byte[] Records) Flatten(R2ModeStats mode) =>
        ([.. mode.Segments.SelectMany(s => s.Types)], [.. mode.Segments.SelectMany(s => s.Records)]);

    /// <summary>
    /// The newest record of a section type, or the start of the run it opens when the run
    /// could not be split (enough to read fields at the start of the record).
    /// </summary>
    public static byte[]? RecordOf(R2ModeStats mode, byte type)
    {
        for (int i = mode.Segments.Count - 1; i >= 0; i--)
        {
            R2StatSegment segment = mode.Segments[i];
            if (segment.Types.Length > 0 && segment.Types[0] == type)
                return segment.Records;
        }
        return null;
    }
}
