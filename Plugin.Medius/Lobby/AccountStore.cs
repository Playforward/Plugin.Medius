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
