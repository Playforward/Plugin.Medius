using Plugin.Medius.RT.Common;

namespace Plugin.Medius.Lobby;

/// <summary>An account's buddies, the players it ignores, and the buddy invitations waiting for it.</summary>
public sealed class BuddyLists
{
    public List<int> Buddies { get; set; } = [];
    public List<int> Ignored { get; set; } = [];
    public List<BuddyInvitation> Invitations { get; set; } = [];
}

/// <summary>A buddy invitation from <see cref="FromAccountId"/>, kept until it is answered.</summary>
public sealed class BuddyInvitation
{
    public int FromAccountId { get; set; }
    public MediusBuddyAddType AddType { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Buddy and ignore lists per account (AccountDataStore key <see cref="Key"/>),
/// so they outlive sessions and server restarts.
/// </summary>
public sealed class BuddyStore(AccountDataStore data)
{
    public const string Key = "buddies";

    private readonly object _lock = new();

    public BuddyLists Get(string titleId, int accountId) => data.Read<BuddyLists>(titleId, accountId, Key) ?? new BuddyLists();

    /// <summary>Changes an account's lists and saves them; returns what <paramref name="change"/> returned.</summary>
    public T Update<T>(string titleId, int accountId, Func<BuddyLists, T> change)
    {
        lock (_lock)
        {
            BuddyLists lists = Get(titleId, accountId);
            T result = change(lists);
            data.Write(titleId, accountId, Key, lists);
            return result;
        }
    }
}
