using System.Collections.Concurrent;

namespace Plugin.Medius.Lobby;

/// <summary>
/// Everything the components share: who has a session, who is logged in,
/// and which channels and game worlds exist. One instance for the whole
/// plugin; channels and games are kept per title so games never see each
/// other's lobbies.
/// </summary>
public sealed class LobbyState(AccountStore accounts)
{
    private readonly ConcurrentDictionary<string, MediusClient> _bySessionKey = new();
    private readonly ConcurrentDictionary<MediusTitle, List<MediusChannel>> _channels = new();
    private readonly ConcurrentDictionary<MediusTitle, ConcurrentDictionary<int, MediusGame>> _games = new();
    private int _nextChannelId = 1;
    private int _nextGameId = 0;

    public AccountStore Accounts { get; } = accounts;

    public MediusClient BeginSession(MediusTitle title, int appId, int mediusVersion)
    {
        MediusClient client = new(title, appId, mediusVersion);
        _bySessionKey[client.SessionKey] = client;
        return client;
    }

    public void EndSession(MediusClient client)
    {
        if (client.GameSeat is { Connection: null } seat)
            LeaveGame(seat);
        client.Logout();
        _bySessionKey.TryRemove(client.SessionKey, out _);
    }

    public MediusClient? BySessionKey(string? sessionKey) =>
        sessionKey is not null && _bySessionKey.TryGetValue(sessionKey, out MediusClient? client) ? client : null;

    public MediusClient? ByAccessToken(string? token) =>
        string.IsNullOrEmpty(token) ? null : _bySessionKey.Values.FirstOrDefault(c => c.AccessToken == token);

    public MediusClient? LoggedIn(MediusTitle title, string accountName) =>
        _bySessionKey.Values.FirstOrDefault(c => c.Title == title && c.IsLoggedIn &&
            string.Equals(c.AccountName, accountName, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<MediusClient> Clients(MediusTitle title) => _bySessionKey.Values.Where(c => c.Title == title);

    public IReadOnlyList<MediusChannel> Channels(MediusTitle title) =>
        _channels.GetOrAdd(title, CreateDefaultChannels);

    public MediusChannel DefaultChannel(MediusTitle title) => Channels(title)[0];

    public MediusChannel? Channel(MediusTitle title, int id) => Channels(title).FirstOrDefault(c => c.Id == id);

    /// <summary>Drops sessions nobody has used for a while (a client that crashed between MAS and MLS).</summary>
    public int Sweep(TimeSpan idle)
    {
        DateTime cutoff = DateTime.UtcNow - idle;
        int removed = 0;
        foreach (MediusClient client in _bySessionKey.Values.Where(c => c.Connection is null && c.GameSeat?.Connection is null && c.LastSeenUtc < cutoff))
        {
            EndSession(client);
            removed++;
        }
        return removed;
    }

    public IEnumerable<MediusGame> Games(MediusTitle title) =>
        _games.TryGetValue(title, out var games) ? games.Values.OrderBy(g => g.Id) : [];

    public MediusGame? Game(MediusTitle title, int id) =>
        _games.TryGetValue(title, out var games) && games.TryGetValue(id, out MediusGame? game) ? game : null;

    /// <summary>A game of any title, by world id (ids are unique across titles).</summary>
    public MediusGame? GameByWorldId(int id) =>
        _games.Values.Select(g => g.GetValueOrDefault(id)).FirstOrDefault(g => g is not null);

    /// <summary>
    /// Creates a game world, pending until its host reports it open. Fails
    /// (null) when the title already has an open game of that name whose host
    /// is still in it.
    /// </summary>
    public MediusGame? CreateGame(MediusTitle title, Func<int, MediusGame> create)
    {
        MediusGame game = create(Interlocked.Increment(ref _nextGameId));
        var games = _games.GetOrAdd(title, _ => new ConcurrentDictionary<int, MediusGame>());
        lock (games)
        {
            if (games.Values.Any(g => g.Status != RT.Common.MediusWorldStatus.WorldClosed && g.Host is not null &&
                                      string.Equals(g.Name, game.Name, StringComparison.OrdinalIgnoreCase)))
                return null;
            games[game.Id] = game;
        }
        return game;
    }

    /// <summary>
    /// Seats the client in the game, leaving any other game it had a seat in
    /// but had not connected to yet. Null when the game is full.
    /// </summary>
    public DmePlayer? JoinGame(MediusClient client, MediusGame game)
    {
        if (client.GameSeat is { } previous && previous.Game != game)
            LeaveGame(previous);
        DmePlayer? seat = game.Reserve(client);
        if (seat is not null)
            client.GameSeat = seat;
        return seat;
    }

    public DmePlayer? SeatByAccessKey(string? accessKey) =>
        string.IsNullOrEmpty(accessKey) ? null :
            _games.Values.SelectMany(g => g.Values).SelectMany(g => g.Players).FirstOrDefault(p => p.AccessKey == accessKey);

    /// <summary>Frees the seat; a game that has been played and is now empty ends.</summary>
    public void LeaveGame(DmePlayer seat)
    {
        MediusGame game = seat.Game;
        game.Remove(seat);
        if (seat.Client.GameSeat == seat)
            seat.Client.GameSeat = null;
        if (game.Players.Length == 0 && (game.HasStarted || game.Status == RT.Common.MediusWorldStatus.WorldClosed))
            EndGame(game);
    }

    public void EndGame(MediusGame game)
    {
        game.Close();
        foreach (DmePlayer seat in game.Players)
        {
            game.Remove(seat);
            if (seat.Client.GameSeat == seat)
                seat.Client.GameSeat = null;
        }
        if (_games.TryGetValue(game.Title, out var games))
            games.TryRemove(game.Id, out _);
    }

    /// <summary>
    /// Frees seats nobody took up at the DME, and ends games nobody ever
    /// connected to. Returns the seats freed and games ended.
    /// </summary>
    public (int Seats, int Games) SweepGames(TimeSpan seatTimeout, TimeSpan gameTimeout)
    {
        DateTime now = DateTime.UtcNow;
        int seats = 0, ended = 0;
        foreach (MediusGame game in _games.Values.SelectMany(g => g.Values).ToList())
        {
            foreach (DmePlayer seat in game.Players.Where(p => p.Connection is null && now - p.ReservedUtc > seatTimeout))
            {
                LeaveGame(seat);
                seats++;
            }
            bool abandoned = game.Players.Length == 0 && !game.HasStarted && now - game.CreatedUtc > gameTimeout;
            bool over = game.Players.Length == 0 && (game.HasStarted || game.Status == RT.Common.MediusWorldStatus.WorldClosed);
            if ((abandoned || over) && Game(game.Title, game.Id) is not null)
            {
                EndGame(game);
                ended++;
            }
        }
        return (seats, ended);
    }

    private List<MediusChannel> CreateDefaultChannels(MediusTitle title)
    {
        IEnumerable<MediusChannelDefinition> definitions = title.Channels.Count > 0
            ? title.Channels
            : [new MediusChannelDefinition { Name = title.Descriptor.Name is { Length: > 0 } name ? name : "Lobby" }];
        return definitions
            .Select(d => new MediusChannel(Interlocked.Increment(ref _nextChannelId), title, d.Name, d.MaxPlayers))
            .ToList();
    }
}
