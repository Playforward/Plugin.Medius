using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;

namespace Plugin.Medius.Lobby;

/// <summary>
/// A title's matchmaking settings (<c>"Matchmaking"</c> in its title.json):
/// the supersets it offers and how party requests are pooled and matched.
/// Everything game-specific is data here; the matchmaker itself knows no game.
/// </summary>
public sealed class MediusMatchmakingSettings
{
    /// <summary>What MediusMatchListSupersets answers, in order.</summary>
    public List<MediusSuperset> Supersets { get; set; } = [];

    /// <summary>
    /// The byte ranges of a party request's attributes blob that must be equal
    /// for two parties to share a pool (e.g. the game type). The pool key is
    /// the superset id and these bytes in hex. Empty: one pool per superset.
    /// </summary>
    public List<MediusAttributeRange> PoolKey { get; set; } = [];

    /// <summary>
    /// Name of a matchmade game. <c>{pool}</c> is the pool's name, <c>{id}</c> the
    /// world id, <c>{hex10}</c> ten random lowercase hex digits. Empty: "{pool} {id}".
    /// PSRewired's R2 server names them "RANKED-c3187bff61" [live R2 1.60].
    /// </summary>
    public string GameName { get; set; } = "";

    /// <summary>The name of matchmade game <paramref name="id"/> from <paramref name="pool"/>.</summary>
    public string GameNameFor(MediusMatchPool pool, int id)
    {
        string poolName = pool.Name.Length > 0 ? pool.Name : "Match";
        if (GameName.Length == 0)
            return $"{poolName} {id}";
        return GameName.Replace("{pool}", poolName).Replace("{id}", id.ToString())
            .Replace("{hex10}", Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(5)).ToLowerInvariant());
    }

    /// <summary>Settings for a pool no entry in <see cref="Pools"/> names.</summary>
    public MediusMatchPool Default { get; set; } = new();

    /// <summary>Per-pool settings, by superset and key (the hex of the <see cref="PoolKey"/> bytes).</summary>
    public List<MediusMatchPool> Pools { get; set; } = [];

    public MediusMatchPool PoolFor(uint supersetId, string key) =>
        Pools.FirstOrDefault(p => p.Superset == supersetId && string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? Pools.FirstOrDefault(p => p.Superset == supersetId && string.IsNullOrEmpty(p.Key))
        ?? Default;

    /// <summary>
    /// Each party's MatchFoundResponse lists only that party, as one team whose
    /// members carry their team number (PSRewired's R2 server: "95650/1" for the
    /// second party of a match [live R2 1.60]). Off: every team of the match, members tagged 0.
    /// </summary>
    public bool RosterPerParty { get; set; }

    /// <summary>The pool key of a request: its attribute bytes named by <see cref="PoolKey"/>, in hex.</summary>
    public string KeyOf(byte[] attributes) => string.Concat(PoolKey.Select(r =>
        r.Offset + r.Size <= attributes.Length ? Convert.ToHexString(attributes, r.Offset, r.Size) : new string('-', 2 * r.Size)));
}

public sealed class MediusSuperset
{
    public uint Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ExtraInfo { get; set; } = "";
}

public sealed class MediusAttributeRange
{
    public int Offset { get; set; }
    public int Size { get; set; }
    /// <summary>What the bytes are, for the log and the docs.</summary>
    public string Name { get; set; } = "";
}

public sealed class MediusMatchPool
{
    public uint Superset { get; set; }
    /// <summary>The pool key (hex) this entry is for; empty matches any key of the superset.</summary>
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Players needed before a game is made.</summary>
    public int MinPlayers { get; set; } = 2;
    /// <summary>Players one game seats.</summary>
    public int MaxPlayers { get; set; } = 8;
    /// <summary>Teams the roster is split into (1 for free-for-all or co-op).</summary>
    public int Teams { get; set; } = 1;
    /// <summary>The options blob the match result carries (hex), read by the game; empty sends none.</summary>
    public string GameOptions { get; set; } = "";
    /// <summary>
    /// The options blob built from parts instead of <see cref="GameOptions"/>
    /// (used when set): literal bytes, per-match fields and text, in order.
    /// </summary>
    public List<MediusBlobPart> GameOptionsParts { get; set; } = [];
    /// <summary>How the matched world is hosted; DME client-server with aux UDP unless the game says otherwise.</summary>
    public MediusGameHostType HostType { get; set; } = MediusGameHostType.MediusGameHostClientServerAuxUDP;
    /// <summary>Text the options blob's <see cref="MediusBlobPart.Lines"/> name as <c>{Key}</c>.</summary>
    public Dictionary<string, string> Values { get; set; } = [];

    /// <summary>
    /// What a game of this pool can be (mode, map, size, ...): each new game is
    /// one of them, picked at random by weight, and keeps it for every party
    /// sent there later. Empty: every game is the pool itself.
    /// </summary>
    public List<MediusMatchVariant> Variants { get; set; } = [];

    /// <summary>The settings for a new game: the pool with one of its <see cref="Variants"/> applied, or the pool itself.</summary>
    public MediusMatchPool ForGame(Random random)
    {
        List<MediusMatchVariant> variants = Variants.Where(v => v.Weight > 0).ToList();
        if (variants.Count == 0)
            return this;
        double roll = random.NextDouble() * variants.Sum(v => v.Weight);
        MediusMatchVariant variant = variants[^1];
        foreach (MediusMatchVariant candidate in variants)
            if ((roll -= candidate.Weight) < 0)
            {
                variant = candidate;
                break;
            }

        bool ownOptions = variant.GameOptions.Length > 0 || variant.GameOptionsParts.Count > 0;
        return new MediusMatchPool
        {
            Superset = Superset,
            Key = Key,
            Name = variant.Name.Length > 0 ? variant.Name : Name,
            MinPlayers = MinPlayers,
            MaxPlayers = variant.MaxPlayers > 0 ? variant.MaxPlayers : MaxPlayers,
            Teams = variant.Teams > 0 ? variant.Teams : Teams,
            GameOptions = ownOptions ? variant.GameOptions : GameOptions,
            GameOptionsParts = ownOptions ? variant.GameOptionsParts : GameOptionsParts,
            HostType = HostType,
            Values = new Dictionary<string, string>(Values.Concat(variant.Values).GroupBy(p => p.Key).Select(g => g.Last())),
        };
    }
}

/// <summary>One kind of game a pool makes (see <see cref="MediusMatchPool.Variants"/>); unset fields are the pool's.</summary>
public sealed class MediusMatchVariant
{
    public string Name { get; set; } = "";
    public int MaxPlayers { get; set; }
    public int Teams { get; set; }
    /// <summary>How often it is picked, relative to the pool's other variants.</summary>
    public double Weight { get; set; } = 1;
    /// <summary>Added to (and overriding) the pool's <see cref="MediusMatchPool.Values"/>.</summary>
    public Dictionary<string, string> Values { get; set; } = [];
    public string GameOptions { get; set; } = "";
    public List<MediusBlobPart> GameOptionsParts { get; set; } = [];
}

/// <summary>
/// One piece of a game-defined blob: exactly one of <see cref="Hex"/>,
/// <see cref="Field"/> or <see cref="Lines"/>.
/// </summary>
public sealed class MediusBlobPart
{
    /// <summary>Literal bytes in hex.</summary>
    public string Hex { get; set; } = "";
    /// <summary>A per-match value: <c>MatchId</c> (the matched world's id) or <c>PlayerCount</c>.</summary>
    public string Field { get; set; } = "";
    /// <summary>How <see cref="Field"/> is written: u8, u16be, u16le, u32be or u32le.</summary>
    public string Encoding { get; set; } = "u32le";
    /// <summary>Text lines, joined by <see cref="LineEnding"/> (UTF-8).</summary>
    public List<string> Lines { get; set; } = [];
    public string LineEnding { get; set; } = "\r\n";
    /// <summary>Ends the text with a NUL byte (the game reads it as a C string).</summary>
    public bool NulTerminated { get; set; }

    public byte[] Build(MediusMatch match)
    {
        if (Hex.Length > 0)
            return Convert.FromHexString(Hex.Replace(" ", ""));
        if (Field.Length > 0)
        {
            long value = Field switch
            {
                "MatchId" => match.Game.Id,
                "PlayerCount" => match.Parties.Sum(p => p.Party.Size),
                _ => throw new InvalidOperationException($"unknown blob field {Field}"),
            };
            byte[] bytes = Encoding switch
            {
                "u8" => [(byte)value],
                "u16be" => [(byte)(value >> 8), (byte)value],
                "u16le" => [(byte)value, (byte)(value >> 8)],
                "u32be" => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value],
                "u32le" => [(byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)],
                _ => throw new InvalidOperationException($"unknown blob encoding {Encoding}"),
            };
            return bytes;
        }
        string joined = string.Join(LineEnding, Lines);
        foreach ((string key, string value) in match?.Pool is { } pool ? Placeholders(pool) : [])
            joined = joined.Replace("{" + key + "}", value);
        byte[] text = System.Text.Encoding.UTF8.GetBytes(joined);
        return NulTerminated ? [.. text, 0] : text;
    }

    /// <summary>What <c>{Key}</c> in <see cref="Lines"/> stands for: the pool's Name, MaxPlayers and Teams, then its Values.</summary>
    private static IEnumerable<(string, string)> Placeholders(MediusMatchPool pool) =>
        new[] { ("Name", pool.Name), ("MaxPlayers", pool.MaxPlayers.ToString()), ("Teams", pool.Teams.ToString()) }
            .Concat(pool.Values.Select(p => (p.Key, p.Value)));
}

/// <summary>A party waiting in a pool: the request that queued it and who it is for.</summary>
public sealed record MediusMatchEntry(MediusClient Requester, MessageId MessageID, uint SupersetId, string Key,
    IReadOnlyList<int> AccountIds, byte[] Attributes, DateTime QueuedUtc)
{
    public int Size => Math.Max(1, AccountIds.Count);
}

/// <summary>A game the matchmaker made, and which party got which team.</summary>
public sealed record MediusMatch(MediusGame Game, MediusMatchPool Pool, IReadOnlyList<(MediusMatchEntry Party, int Team)> Parties)
{
    /// <summary>The options blob the match result carries: the pool's parts, else its hex, else none.</summary>
    public byte[] GameOptions() => Pool.GameOptionsParts.Count > 0
        ? Pool.GameOptionsParts.SelectMany(p => p.Build(this)).ToArray()
        : Pool.GameOptions.Length > 0 ? Convert.FromHexString(Pool.GameOptions) : [];

    /// <summary>The roster the match result carries: each team's account ids.</summary>
    public List<MediusMatchTeam> Roster(int teams) => Enumerable.Range(0, Math.Max(1, teams)).Select(t => new MediusMatchTeam
    {
        Members = Parties.Where(p => p.Team == t)
            .SelectMany(p => MembersOf(p.Party))
            .Select(id => new MediusMatchTeamMember { AccountID = id })
            .ToList(),
    }).ToList();

    /// <summary>The roster one party's result carries: the whole match's, or (<see cref="MediusMatchmakingSettings.RosterPerParty"/>) its own.</summary>
    public List<MediusMatchTeam> RosterFor(MediusMatchEntry party, MediusMatchmakingSettings settings)
    {
        if (!settings.RosterPerParty)
            return Roster(Pool.Teams);
        int team = Parties.First(p => p.Party == party).Team;
        return [new MediusMatchTeam { Members = MembersOf(party).Select(id => new MediusMatchTeamMember { AccountID = id, Unk00 = (uint)team }).ToList() }];
    }

    private static IReadOnlyList<int> MembersOf(MediusMatchEntry party) =>
        party.AccountIds.Count > 0 ? party.AccountIds : [party.Requester.AccountId];
}

/// <summary>
/// The Medius Match Server's queue: parties wait in pools (superset + the
/// title's pool key) and are matched first come, first served, whole
/// parties at a time, once a pool holds the pool's minimum. A match creates
/// a game world that the matched players then join like any other.
/// </summary>
public sealed class MediusMatchmaker
{
    private readonly object _gate = new();
    private readonly List<MediusMatchEntry> _queue = [];

    public IReadOnlyList<MediusMatchEntry> Queue
    {
        get { lock (_gate) return _queue.ToList(); }
    }

    /// <summary>Queues the party, replacing any earlier request of the same requester.</summary>
    public MediusMatchEntry Enqueue(MediusClient requester, MediusMatchPartyRequest request)
    {
        MediusMatchmakingSettings settings = requester.Title.Matchmaking;
        MediusMatchEntry entry = new(requester, request.MessageID, request.SupersetID, settings.KeyOf(request.Attributes ?? []),
            (request.PlayerAccountIDs ?? []).Select(id => (int)id).ToList(), request.Attributes ?? [], DateTime.UtcNow);
        lock (_gate)
        {
            _queue.RemoveAll(e => e.Requester == requester);
            _queue.Add(entry);
        }
        return entry;
    }

    /// <summary>Drops the requester's party from the queue; false when it was not queued.</summary>
    public bool Cancel(MediusClient requester)
    {
        lock (_gate)
            return _queue.RemoveAll(e => e.Requester == requester) > 0;
    }

    /// <summary>
    /// Makes every match the queue allows, per pool, oldest parties first and
    /// whole parties at a time: first into the pool's games still open for late
    /// join (<paramref name="games"/>, fullest first, up to each one's maximum),
    /// then into new games of up to the pool's maximum once at least its minimum
    /// is waiting. <paramref name="createGame"/> makes a world (null: it could
    /// not, and the parties stay queued).
    /// </summary>
    public List<MediusMatch> TryMatch(MediusTitle title, IEnumerable<MediusGame> games,
        Func<MediusMatchPool, IReadOnlyList<MediusMatchEntry>, MediusGame?> createGame, Random? random = null)
    {
        random ??= Random.Shared;
        List<MediusMatch> matches = [];
        lock (_gate)
        {
            _queue.RemoveAll(e => e.Requester.Connection is null || !e.Requester.IsLoggedIn);
            List<MediusGame> open = games.Where(g => g.MatchPool is not null && g.AcceptsLateJoin &&
                                                     g.Status != MediusWorldStatus.WorldClosed && !(g.HasStarted && g.Players.Length == 0)).ToList();
            foreach (var pool in _queue.Where(e => e.Requester.Title == title).GroupBy(e => (e.SupersetId, e.Key)).ToList())
            {
                MediusMatchPool settings = title.Matchmaking.PoolFor(pool.Key.SupersetId, pool.Key.Key);
                List<MediusMatchEntry> waiting = pool.OrderBy(e => e.QueuedUtc).ToList();

                foreach (MediusGame game in open.Where(g => g.MatchPool == (pool.Key.SupersetId, pool.Key.Key)).OrderByDescending(Seated))
                {
                    List<MediusMatchEntry> picked = Pick(waiting, game.MaxPlayers - Seated(game));
                    if (picked.Count == 0)
                        continue;
                    MediusMatchPool made = game.MatchSettings ?? settings;
                    Matched(new MediusMatch(game, made, AssignTeams(picked, made.Teams, game.MatchTeamSizes)), picked, waiting);
                }

                while (waiting.Sum(e => e.Size) >= Math.Max(1, settings.MinPlayers))
                {
                    MediusMatchPool chosen = settings.ForGame(random);
                    List<MediusMatchEntry> picked = Pick(waiting, Math.Max(1, chosen.MaxPlayers));
                    if (picked.Sum(e => e.Size) < Math.Max(1, settings.MinPlayers) || createGame(chosen, picked) is not { } game)
                        break;
                    game.MatchPool = (pool.Key.SupersetId, pool.Key.Key);
                    game.MatchSettings = chosen;
                    Matched(new MediusMatch(game, chosen, AssignTeams(picked, chosen.Teams, game.MatchTeamSizes)), picked, waiting);
                }
            }
        }
        return matches;

        void Matched(MediusMatch match, List<MediusMatchEntry> picked, List<MediusMatchEntry> waiting)
        {
            int[] sizes = match.Game.MatchTeamSizes.Length >= Math.Max(1, match.Pool.Teams)
                ? match.Game.MatchTeamSizes
                : [.. match.Game.MatchTeamSizes, .. new int[Math.Max(1, match.Pool.Teams) - match.Game.MatchTeamSizes.Length]];
            foreach ((MediusMatchEntry party, int team) in match.Parties)
                sizes[team] += party.Size;
            match.Game.MatchTeamSizes = sizes;
            matches.Add(match);
            foreach (MediusMatchEntry party in picked)
            {
                waiting.Remove(party);
                _queue.Remove(party);
            }
        }
    }

    /// <summary>
    /// Seats a game holds or has promised: its seated players, or the players
    /// sent to it (who take their seats a moment later), whichever is more.
    /// </summary>
    private static int Seated(MediusGame game) => Math.Max(game.Players.Length, game.MatchTeamSizes.Sum());

    /// <summary>Oldest whole parties first, as many as fit in <paramref name="room"/>.</summary>
    private static List<MediusMatchEntry> Pick(List<MediusMatchEntry> waiting, int room)
    {
        List<MediusMatchEntry> picked = [];
        foreach (MediusMatchEntry party in waiting)
            if (picked.Sum(e => e.Size) + party.Size <= room)
                picked.Add(party);
        return picked;
    }

    /// <summary>
    /// Largest parties first, each onto the team with the fewest players
    /// (counting <paramref name="already"/>, the players a game already has per
    /// team); parties stay together.
    /// </summary>
    internal static List<(MediusMatchEntry Party, int Team)> AssignTeams(IEnumerable<MediusMatchEntry> parties, int teams, int[]? already = null)
    {
        int[] sizes = new int[Math.Max(1, teams)];
        if (already is not null)
            Array.Copy(already, sizes, Math.Min(already.Length, sizes.Length));
        List<(MediusMatchEntry, int)> assigned = [];
        foreach (MediusMatchEntry party in parties.OrderByDescending(p => p.Size).ThenBy(p => p.QueuedUtc))
        {
            int team = Array.IndexOf(sizes, sizes.Min());
            sizes[team] += party.Size;
            assigned.Add((party, team));
        }
        return assigned;
    }
}
