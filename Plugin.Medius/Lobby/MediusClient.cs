using Plugin.Medius.Transport;
using System.Security.Cryptography;

namespace Plugin.Medius.Lobby;

/// <summary>
/// A player's Medius session. It outlives any one TCP connection: the client
/// begins it on the MAS, logs in there, then reconnects to the MLS carrying
/// the session key and access token it was given, and is found again by them.
/// </summary>
public sealed class MediusClient
{
    private static int _nextSessionKey = RandomNumberGenerator.GetInt32(10000, 20000);

    public MediusClient(MediusTitle title, int appId, int mediusVersion)
    {
        Title = title;
        AppId = appId;
        MediusVersion = mediusVersion;
        SessionKey = Interlocked.Increment(ref _nextSessionKey).ToString();
    }

    public MediusTitle Title { get; }
    public int AppId { get; }
    public int MediusVersion { get; set; }

    /// <summary>Up to 16 characters; the client echoes it in most requests.</summary>
    public string SessionKey { get; }

    /// <summary>Issued at login, presented on connect to the next component. Up to 16 characters.</summary>
    public string AccessToken { get; private set; } = "";

    public MediusAccount? Account { get; private set; }
    public int AccountId => Account?.AccountId ?? -1;
    public string AccountName => Account?.Name ?? "";
    public bool IsLoggedIn => Account is not null;

    public MediusChannel? Channel { get; set; }

    /// <summary>The seat the player holds in a game world, from joining it on the MLS until leaving the DME.</summary>
    public DmePlayer? GameSeat { get; internal set; }
    public MediusGame? Game => GameSeat?.Game;

    /// <summary>The player's game list filters (MediusSetGameListFilter); the list shows games meeting all of them.</summary>
    public List<GameListFilter> GameListFilters { get; } = [];
    internal uint NextGameListFilterId { get; set; } = 1;

    /// <summary>The connection this player currently talks to the lobby on, if any.</summary>
    public ScertConnection? Connection { get; internal set; }

    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Title extensions keep their per-player state here.</summary>
    public Dictionary<string, object> ExtensionState { get; } = [];

    public void Login(MediusAccount account)
    {
        Account = account;
        AccessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(8))[..16];
    }

    public void Logout()
    {
        Account = null;
        AccessToken = "";
        Channel?.Leave(this);
        Channel = null;
    }

    public override string ToString() => IsLoggedIn ? $"{AccountName}#{AccountId}" : $"session {SessionKey}";
}
