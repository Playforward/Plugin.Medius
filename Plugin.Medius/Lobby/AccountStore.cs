using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Plugin.Medius.Lobby;

/// <summary>A Medius account. Accounts belong to a title, not to one region's app id.</summary>
public sealed class MediusAccount
{
    public int AccountId { get; set; }
    public string TitleId { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>SHA-256 of the password, hex. Empty for ticket (PSN) accounts, which have none.</summary>
    public string PasswordHash { get; set; } = "";
    /// <summary>The 256-byte account stats blob the game reads and writes, base64.</summary>
    public string Stats { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastLoginUtc { get; set; }
    /// <summary>
    /// The PSN/RPCN account id from the player's NP ticket (ticket param 4), once a
    /// service that sees the ticket has learnt it; 0 until then. Some games address
    /// players by it rather than by Medius account id (Resistance 2's stats requests).
    /// </summary>
    public ulong NpAccountId { get; set; }

    public bool CheckPassword(string? password) =>
        PasswordHash.Length == 0 || PasswordHash == AccountStore.Hash(password ?? "");
}

/// <summary>
/// Accounts kept in one JSON file under the plugin's data directory.
/// Small and local by design: a LAN or community server has hundreds of
/// accounts, not millions, and a file is something its owner can read.
/// </summary>
public sealed class AccountStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private readonly List<MediusAccount> _accounts;

    public AccountStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _path = Path.Combine(dataDir, "accounts.json");
        _accounts = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<MediusAccount>>(File.ReadAllText(_path)) ?? []
            : [];
    }

    public MediusAccount? Find(string titleId, string name)
    {
        lock (_lock)
            return _accounts.FirstOrDefault(a => a.TitleId == titleId && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public MediusAccount? Find(int accountId)
    {
        lock (_lock)
            return _accounts.FirstOrDefault(a => a.AccountId == accountId);
    }

    /// <summary>The title's account with this NP account id (see <see cref="MediusAccount.NpAccountId"/>).</summary>
    public MediusAccount? FindByNpAccountId(string titleId, ulong npAccountId)
    {
        lock (_lock)
            return npAccountId == 0 ? null : _accounts.FirstOrDefault(a => a.TitleId == titleId && a.NpAccountId == npAccountId);
    }

    /// <summary>
    /// The title's account whose NP account id has these low 32 bits, for clients
    /// that print the id with %d/%u; null when none or more than one match.
    /// </summary>
    public MediusAccount? FindByNpAccountIdLow32(string titleId, uint low)
    {
        lock (_lock)
        {
            List<MediusAccount> found = [.. _accounts.Where(a => a.TitleId == titleId && a.NpAccountId != 0 && (uint)a.NpAccountId == low).Take(2)];
            return found.Count == 1 ? found[0] : null;
        }
    }

    /// <summary>Every account of the title.</summary>
    public IReadOnlyList<MediusAccount> All(string titleId)
    {
        lock (_lock)
            return [.. _accounts.Where(a => a.TitleId == titleId)];
    }

    /// <summary>Creates the account, or returns null when the name is taken for that title.</summary>
    public MediusAccount? Create(string titleId, string name, string? password)
    {
        lock (_lock)
        {
            if (Find(titleId, name) is not null)
                return null;
            MediusAccount account = new()
            {
                AccountId = _accounts.Count == 0 ? 1 : _accounts.Max(a => a.AccountId) + 1,
                TitleId = titleId,
                Name = name,
                PasswordHash = string.IsNullOrEmpty(password) ? "" : Hash(password),
                Stats = Convert.ToBase64String(new byte[256]),
            };
            _accounts.Add(account);
            Save();
            return account;
        }
    }

    public void Update(MediusAccount account, Action<MediusAccount> change)
    {
        lock (_lock)
        {
            change(account);
            Save();
        }
    }

    private void Save()
    {
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_accounts, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }

    internal static string Hash(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));
}
