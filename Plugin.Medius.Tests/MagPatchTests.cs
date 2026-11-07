using Plugin.Medius.Extensions.Mag;
using Plugin.Medius.Extensions.Zipper;
using Plugin.Medius.Web;

namespace Plugin.Medius.Tests;

/// <summary>The launcher's DLS patches: which ones a launcher needs, and how they're served.</summary>
public sealed class MagPatchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("magpatches").FullName;

    public MagPatchTests()
    {
        // 2.12's chain (patch.xml requires/provides), with small stand-ins for the archives.
        File.WriteAllBytes(Path.Combine(_dir, "patch_r000000_445230.psarc"), Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        File.WriteAllBytes(Path.Combine(_dir, "patch_r445230_465304.psarc"), new byte[20]);
        File.WriteAllBytes(Path.Combine(_dir, "patch_r465304_467225.psarc"), new byte[30]);
        File.WriteAllBytes(Path.Combine(_dir, "patch_r467225_489979.psarc"), new byte[40]);
        File.WriteAllBytes(Path.Combine(_dir, "readme.txt"), new byte[1]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_launcher_without_patches_needs_the_whole_chain_in_order()
    {
        MagPatches patches = new(_dir, "/mag/patches");
        Assert.Equal(4, patches.Files.Count);   // readme.txt isn't a patch
        Assert.Equal(["patch_r000000_445230.psarc", "patch_r445230_465304.psarc", "patch_r465304_467225.psarc", "patch_r467225_489979.psarc"],
            patches.Needed([]).Select(f => f.Name));
    }

    /// <summary>The 2.12 launcher with every patch reported [445230, 465304, 467225, 489979] (live): nothing to fetch.</summary>
    [Fact]
    public void A_launcher_with_every_patch_needs_none()
    {
        MagPatches patches = new(_dir, "/mag/patches");
        Assert.Empty(patches.Needed([445230, 465304, 467225, 489979]));
        Assert.Equal(["patch_r465304_467225.psarc", "patch_r467225_489979.psarc"], patches.Needed([445230, 465304]).Select(f => f.Name));
    }

    [Fact]
    public void A_range_request_gets_that_slice()
    {
        MagPatches patches = new(_dir, "/mag/patches");
        MediusWebResponse response = patches.Serve(new MediusWebRequest
        {
            Target = "/mag/patches/patch_r000000_445230.psarc",
            Headers = new Dictionary<string, string> { ["range"] = "bytes=10-19" },
        })!;
        Assert.Equal(206, response.Status);
        Assert.Equal(Enumerable.Range(10, 10).Select(i => (byte)i), response.Body);
        Assert.Equal("bytes 10-19/100", response.Headers["Content-Range"]);

        MediusWebResponse whole = patches.Serve(new MediusWebRequest { Target = "/mag/patches/patch_r000000_445230.psarc" })!;
        Assert.Equal((200, 100), (whole.Status, whole.Body.Length));
        Assert.Null(patches.Serve(new MediusWebRequest { Target = "/elsewhere/patch_r000000_445230.psarc" }));
        Assert.Equal(404, patches.Serve(new MediusWebRequest { Target = "/mag/patches/readme.txt" })!.Status);
    }

    [Theory]
    [InlineData("bytes=0-", 0L, 99L)]
    [InlineData("bytes=90-200", 90L, 99L)]
    [InlineData("bytes=-10", 90L, 99L)]
    public void Ranges_are_clamped_to_the_file(string header, long start, long end) =>
        Assert.Equal((start, end), MagPatches.ParseRange(header, 100));

    [Fact]
    public void Unsatisfiable_ranges_are_refused() =>
        Assert.Null(MagPatches.ParseRange("bytes=100-", 100));

    /// <summary>2.12's reply: PatchSettings, then one PatchURL per file with its URL, kind and size, the last flagged.</summary>
    [Fact]
    public void Patch_reply_lists_each_file_and_flags_the_last()
    {
        MagClientVersion patch = MagClientVersion.Patch212;
        IReadOnlyList<byte[]> messages = MapsWriters.PatchReply(patch,
            [new MapsPatchFile { Name = "http://h:1/mag/patches/a.psarc", Kind = 2, Size = 5 }, new MapsPatchFile { Name = "http://h:1/mag/patches/b.psarc", Kind = 2, Size = 6 }]);
        Assert.Equal(3, messages.Count);
        ZipperBitReader last = new(messages[2]);
        Assert.Equal(patch.Messages.PatchUrl, last.ReadU16());
        Assert.Equal("http://h:1/mag/patches/b.psarc", last.ReadString());
        Assert.Equal(2, last.ReadU8());
        Assert.Equal(6u, last.ReadU32());
        Assert.True(last.ReadBool());
    }
}
