using Plugin.Medius.Lobby;

namespace Plugin.Medius.Extensions.Killzone2;

/// <summary>
/// What a player's game reports of its profile when it syncs (profile/sync,
/// before a match): the online score and each ribbon's amount, by the names the
/// game's ribbons resource gives them [live 2026-10-31: MPScore=0&amp;Kill=0&amp;
/// GoodConduct=0...]. Kept per account (AccountDataStore key <see cref="Key"/>)
/// for the leaderboards.
/// </summary>
public sealed class Killzone2Profile
{
    public int MPScore { get; set; }
    public Dictionary<string, int> Ribbons { get; set; } = [];
    public DateTime UpdatedUtc { get; set; }

    /// <summary>
    /// Set by the server's owner to lift the player: the rank id (the game's ranks resource,
    /// GetScoreForRank; 2 = Sergeant, the clan menu's bound in menu.jsp) and/or score the player's
    /// profile is raised to when they sign in (svo/killzone2/home.svml, SyncMPScore). Kept across uploads.
    /// </summary>
    public int? MinRank { get; set; }
    public int? MinScore { get; set; }
}

/// <summary>The synced profiles of a title's players (see <see cref="Killzone2Profile"/>).</summary>
public sealed class Killzone2Profiles(AccountDataStore data, string titleId, Func<int, string?> nameOf)
{
    public const string Key = "profile";

    /// <summary>The score field of the upload; every other integer field is a ribbon.</summary>
    public const string ScoreField = "MPScore";

    public Killzone2Profile? Find(int accountId) => data.Read<Killzone2Profile>(titleId, accountId, Key);

    /// <summary>Stores an upload's fields; null when it has no score.</summary>
    public Killzone2Profile? Save(int accountId, IReadOnlyDictionary<string, string> fields)
    {
        if (!fields.TryGetValue(ScoreField, out string? score) || !int.TryParse(score, out int mpScore))
            return null;
        Killzone2Profile? before = Find(accountId);
        Killzone2Profile profile = new() { MPScore = mpScore, UpdatedUtc = DateTime.UtcNow, MinRank = before?.MinRank, MinScore = before?.MinScore };
        foreach ((string name, string value) in fields)
            if (name != ScoreField && int.TryParse(value, out int amount))
                profile.Ribbons[name] = amount;
        data.Write(titleId, accountId, Key, profile);
        return profile;
    }

    /// <summary>
    /// Adds a match's points to the stored score (a profile is started if there is none), so the
    /// leaderboards show them before the game's next upload, which then sends the same total.
    /// </summary>
    public Killzone2Profile AddScore(int accountId, int points)
    {
        lock (_lock)
        {
            Killzone2Profile profile = Find(accountId) ?? new Killzone2Profile();
            profile.MPScore = Math.Max(0, profile.MPScore + points);
            profile.UpdatedUtc = DateTime.UtcNow;
            data.Write(titleId, accountId, Key, profile);
            return profile;
        }
    }

    private readonly object _lock = new();

    /// <summary>Every player with a synced profile and a name (or those of <paramref name="only"/>), highest score first (then by name).</summary>
    public IReadOnlyList<(int AccountId, string Name, Killzone2Profile Profile)> Ranked(IEnumerable<int>? only = null) =>
        [.. (only?.Distinct() ?? data.AccountsWith(titleId, Key))
            .Select(id => (AccountId: id, Name: nameOf(id), Profile: Find(id)))
            .Where(p => p.Name is not null && p.Profile is not null)
            .Select(p => (p.AccountId, p.Name!, p.Profile!))
            .OrderByDescending(p => p.Item3.MPScore)
            .ThenBy(p => p.Item2, StringComparer.OrdinalIgnoreCase)];
}
