using Plugin.Medius.Extensions.Killzone2;
using Plugin.Medius.Web;
using Plugin.Medius.Lobby;
using Serilog;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Plugin.Medius.Extensions.ResistanceFallOfMan;

/// <summary>
/// Resistance: Fall of Man (Insomniac, PS3, 2006). Stock Medius for the lobby (SCE-RT 3.98 on the
/// disc, 3.02 after the in-game patch) and Sony's SVO web service for its online pages, with the
/// same svoclient MAC scheme and response key as Killzone 2 (verified on PSRewired captures,
/// 2026-11-01), so it is served by the Killzone 2 SVO service with the title's own settings: an
/// SVML root (<see cref="SvoRootFormat.Svml"/>), account cookies at the ticket login, a
/// Create_Game form at createGameURI. The in-game patch is served by <see cref="PatchService"/>
/// (settings "Patch"). Match results come in at finishGameURI and are kept by
/// <see cref="ResistanceStats"/>. See ServerApp-Titles/ResistanceFallOfMan/medius/PROTOCOL.md.
/// </summary>
public sealed partial class ResistanceExtension : Killzone2Extension
{
    public override string Name => "resistance";

    /// <summary>The players' careers.</summary>
    public ResistanceStats? Stats { get; set; }

    [GeneratedRegex(@"<(\w+) name=""\1"">0</\1>")]
    private static partial Regex StatElement();

    /// <summary>The patch files, when the title configures them.</summary>
    public PatchService? Patch { get; private set; }

    public override void Initialize(MediusTitle title, MediusContext context)
    {
        base.Initialize(title, context);
        if (context is not null)
            Stats = new ResistanceStats(new AccountDataStore(context.Settings.DataDir), title.Descriptor.TitleId);
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
        if (IsPathOf("finishGameURI", request.Path))
            return FinishGame(request);
        if (Patch?.Find(request.Path) is not { } body)
            return null;
        Log.Information("[medius/{Extension}] {Request}: patch file, {Size} bytes", Name, request, body.Length);
        return new MediusWebResponse { ContentType = "application/octet-stream", Body = body };
    }

    /// <summary>
    /// A player's result of a match (form body, 0x367bfc) [C; live]. Each player posts their own, and
    /// one who is dropped posts too (endStatus 4, a loss) [live]. An SVO game counts once per player;
    /// a game the server no longer knows (it was restarted) still counts. The client does not need
    /// the answer to go on: it answered a 404 by going back to the lobby [live].
    /// </summary>
    private MediusWebResponse FinishGame(MediusWebRequest request)
    {
        Dictionary<string, string> form = MediusWebRequest.ParseQuery(Encoding.ASCII.GetString(request.Body));
        int? account = AccountOf(request);
        SvoGame? game = form.TryGetValue("SVOGameID", out string? id) && int.TryParse(id, out int gameId) ? Games.Find(gameId) : null;
        if (account is not { } player || Stats is null)
            Log.Warning("[medius/{Extension}] {Request}: result with no account or no stats store ignored", Name, request);
        else if (game is not null && !game.Results.TryAdd(player, 1))
            Log.Debug("[medius/{Extension}] #{Account}: SVO game {Id} already counted", Name, player, game.Id);
        else
        {
            Stats.Add(player, form);
            Log.Information("[medius/{Extension}] #{Account}: game {Id} mode {Mode} on map {Map}: wins {Wins}, losses {Losses}, kills {Kills}, deaths {Deaths}, rating {Rating}, end status {End}",
                Name, player, game?.Id, form.GetValueOrDefault(ResistanceStats.ModeField), form.GetValueOrDefault("mapID"), form.GetValueOrDefault("wins"), form.GetValueOrDefault("losses"),
                form.GetValueOrDefault("kills"), form.GetValueOrDefault("deaths"), form.GetValueOrDefault("rating"), form.GetValueOrDefault("endStatus"));
        }
        return GameDocument(GamePlayerElement(GameRequestSuccessId));
    }

    /// <summary>Puts the player's career into playerStatsURI's page: each stat element the template leaves at 0 that a value exists for.</summary>
    protected override string FinishPage(string text, MediusWebRequest request, Killzone2PageSettings page)
    {
        if (page.Uri != "playerStatsURI" || Stats is null || request.QueryInt64("PlayerID") is not { } player)
            return text;
        Dictionary<string, string> view = Stats.View((int)player, (int)(request.QueryInt64("gameMode") ?? 0));
        return StatElement().Replace(text, m => view.TryGetValue(m.Groups[1].Value, out string? value)
            ? $"<{m.Groups[1].Value} name=\"{m.Groups[1].Value}\">{value}</{m.Groups[1].Value}>" : m.Value);
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
