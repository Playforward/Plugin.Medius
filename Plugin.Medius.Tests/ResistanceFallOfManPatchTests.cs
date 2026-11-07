using Plugin.Medius.Extensions.ResistanceFallOfMan;
using Plugin.Medius.Web;
using ServerShared.Titles;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Plugin.Medius.Tests;

/// <summary>
/// Resistance: Fall of Man's in-game patch service (Extensions/ResistanceFallOfMan/PatchService): the
/// manifest layout and CRC the disc client checks (addresses in PatchService).
/// </summary>
public sealed class ResistanceFallOfManPatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rfom-patch-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string PatchDir()
    {
        string dir = Path.Combine(_root, "medius", "patch");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "EBOOT.BIN"), Encoding.ASCII.GetBytes("123456789"));
        File.WriteAllBytes(Path.Combine(dir, "patch_01.psarc"), [0x50, 0x53, 0x41, 0x52]);
        return dir;
    }

    /// <summary>CRC-32 of "123456789" is CBF43926; the client keeps the register without the final XOR.</summary>
    [Fact]
    public void Crc_is_crc32_without_the_final_xor()
    {
        Assert.Equal(~0xCBF43926u, PatchService.Crc(new MemoryStream(Encoding.ASCII.GetBytes("123456789"))));
    }

    [Fact]
    public void Manifest_is_big_endian_with_32_byte_entries()
    {
        PatchService patch = new("/rfom/BCUS98107", PatchDir(), ["EBOOT.BIN", "patch_01.psarc"]);
        byte[] m = patch.Manifest;
        Assert.Equal(8 + 2 * 0x20, m.Length);
        Assert.Equal(0x4947, BinaryPrimitives.ReadUInt16BigEndian(m));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(2)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(4)));
        Assert.Equal(9ul, BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(8)));
        Assert.Equal(~0xCBF43926u, BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(16)));
        Assert.Equal("EBOOT.BIN", Encoding.ASCII.GetString(m, 20, 20).TrimEnd('\0'));
        Assert.Equal(4ul, BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(40)));
        Assert.Equal("patch_01.psarc", Encoding.ASCII.GetString(m, 52, 20).TrimEnd('\0'));
    }

    [Fact]
    public void Files_and_manifest_are_found_under_the_path_only()
    {
        PatchService patch = new("rfom/BCUS98107/", PatchDir(), ["EBOOT.BIN"]);
        Assert.Same(patch.Manifest, patch.Find("/rfom/BCUS98107/manifest.dat"));
        Assert.Equal(Encoding.ASCII.GetBytes("123456789"), patch.Find("/rfom/BCUS98107/EBOOT.BIN"));
        Assert.Null(patch.Find("/rfom/BCUS98107/patch_01.psarc")); // not listed
        Assert.Null(patch.Find("/rfom/BCUS98107/../title.json"));
        Assert.Null(patch.Find("/other/manifest.dat"));
    }

    /// <summary>The client fetches "&lt;ExtendedInfo URL&gt;/&lt;name&gt;" through its SVO connection, so the SVO host answers it.</summary>
    [Fact]
    public void Extension_serves_the_patch_on_the_svo_host()
    {
        PatchDir();
        MediusTitle title = new()
        {
            AppIds = [20174],
            Descriptor = new TitleDescriptor { TitleId = "resistance", ResourceDir = _root },
            ExtensionSettings = JsonDocument.Parse("""
                { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test:10091" },
                  "Patch": { "Path": "/rfom/BCUS98107", "Dir": "patch", "Files": [ "EBOOT.BIN", "patch_01.psarc" ] } }
                """).RootElement,
        };
        ResistanceExtension extension = new();
        extension.Initialize(title, null!);
        MediusWebResponse response = extension.HandleWeb(new MediusWebRequest
        {
            Method = "GET", Target = "/rfom/BCUS98107/manifest.dat",
            Headers = new Dictionary<string, string> { ["host"] = "svo.test" },
        })!;
        Assert.Equal("application/octet-stream", response.ContentType);
        Assert.Equal(extension.Patch!.Manifest, response.Body);
    }
}
