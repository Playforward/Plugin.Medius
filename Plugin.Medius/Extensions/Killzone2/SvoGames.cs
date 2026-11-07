using System.Collections.Concurrent;

namespace Plugin.Medius.Extensions.Killzone2;

/// <summary>
/// The games registered with the SVO for stats: the host registers each match
/// it starts (gameCreateURL, GameCreator.cpp in the 1.29 EBOOT) and gets an
/// SVOGameID, which the players' createGamePlayerURI calls and the match's
/// finishGameURI name [C].
/// </summary>
public sealed class SvoGames
{
    private readonly ConcurrentDictionary<int, SvoGame> _games = new();
    private int _nextId;

    public SvoGame Create(string name, int hostAccountId, int gameMode, int timePerDivision, int divisions)
    {
        SvoGame game = new(Interlocked.Increment(ref _nextId), name, hostAccountId, gameMode, timePerDivision, divisions);
        _games[game.Id] = game;
        return game;
    }

    public SvoGame? Find(int id) => _games.GetValueOrDefault(id);
}

public sealed class SvoGame(int id, string name, int hostAccountId, int gameMode, int timePerDivision, int divisions)
{
    public int Id { get; } = id;
    public string Name { get; } = name;
    public int HostAccountId { get; } = hostAccountId;
    public int GameMode { get; } = gameMode;
    public int TimePerDivision { get; } = timePerDivision;
    public int Divisions { get; } = divisions;
    public DateTime CreatedUtc { get; } = DateTime.UtcNow;

    /// <summary>Account id -> side, from createGamePlayerURI.</summary>
    public ConcurrentDictionary<int, int> Players { get; } = new();

    /// <summary>Account id -> points, from each player's gameSubmitURL (the first one counts).</summary>
    public ConcurrentDictionary<int, int> Results { get; } = new();
}
