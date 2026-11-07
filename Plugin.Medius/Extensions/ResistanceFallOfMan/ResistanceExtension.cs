using Plugin.Medius.Extensions.Killzone2;
using Plugin.Medius.Web;
using Serilog;
using System.Text.Json;

namespace Plugin.Medius.Extensions.ResistanceFallOfMan;

/// <summary>
/// Resistance: Fall of Man (Insomniac, PS3, 2006). Stock Medius for the lobby (SCE-RT 3.98 on the
/// disc, 3.02 after the in-game patch) and Sony's SVO web service for its online pages, with the
/// same svoclient MAC scheme and response key as Killzone 2 (verified on PSRewired captures,
/// 2026-11-01), so it is served by the Killzone 2 SVO service with the title's own settings: an
/// SVML root (<see cref="SvoRootFormat.Svml"/>), account cookies at the ticket login, a
/// Create_Game form at createGameURI. The in-game patch is served by <see cref="PatchService"/>
/// (settings "Patch"). See ServerApp-Titles/ResistanceFallOfMan/medius/PROTOCOL.md.
/// </summary>
public sealed class ResistanceExtension : Killzone2Extension
{
    public override string Name => "resistance";

    /// <summary>The patch files, when the title configures them.</summary>
    public PatchService? Patch { get; private set; }

    public override void Initialize(MediusTitle title, MediusContext context)
    {
        base.Initialize(title, context);
        if (title.ExtensionSettings.ValueKind != JsonValueKind.Object ||
            !title.ExtensionSettings.TryGetProperty("Patch", out JsonElement element) ||
            element.Deserialize<ResistancePatchSettings>(MediusTitle.JsonOptions) is not { Files.Count: > 0 } settings)
            return;
        string directory = Path.Combine(title.Descriptor.ResourceDir ?? "", "medius", settings.Dir);
        try
        {
            Patch = new PatchService(settings.Path, directory, settings.Files);
            Log.Information("[medius/{Extension}] patch at {Path}: {Count} files from {Dir}", Name, Patch.Path, settings.Files.Count, directory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Error("[medius/{Extension}] patch not served: {Error} (copy the files to {Dir})", Name, e.Message, directory);
        }
    }

    protected override MediusWebResponse? RouteTitle(MediusWebRequest request)
    {
        if (Patch?.Find(request.Path) is not { } body)
            return null;
        Log.Information("[medius/{Extension}] {Request}: patch file, {Size} bytes", Name, request, body.Length);
        return new MediusWebResponse { ContentType = "application/octet-stream", Body = body };
    }
}

/// <summary>Where the in-game patch is served (see <see cref="PatchService"/>).</summary>
public sealed class ResistancePatchSettings
{
    /// <summary>The path of the ExtendedInfo URL the files are under (/rfom/BCUS98107).</summary>
    public string Path { get; set; } = "";
    /// <summary>The directory under the title's medius folder holding them.</summary>
    public string Dir { get; set; } = "patch";
    /// <summary>The files, in manifest order: EBOOT.BIN and patch_01.psarc..</summary>
    public List<string> Files { get; set; } = [];
}
