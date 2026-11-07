using System.Text.Json;

namespace Plugin.Medius.Lobby;

/// <summary>
/// Documents a title keeps per account (a game's stats, its progress), or once
/// for the title, as JSON files under the data directory:
/// <c>accountdata/&lt;title&gt;/&lt;account id&gt;/&lt;key&gt;.json</c> and
/// <c>accountdata/&lt;title&gt;/title/&lt;key&gt;.json</c>. Reads go to disk, so an
/// owner can edit a file while the server runs.
/// </summary>
public sealed class AccountDataStore(string dataDir)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly object _lock = new();

    public T? Read<T>(string titleId, int accountId, string key) where T : class => ReadFile<T>(PathOf(titleId, accountId.ToString(), key));

    public void Write<T>(string titleId, int accountId, string key, T value) => WriteFile(PathOf(titleId, accountId.ToString(), key), value);

    public T? ReadTitle<T>(string titleId, string key) where T : class => ReadFile<T>(PathOf(titleId, "title", key));

    public void WriteTitle<T>(string titleId, string key, T value) => WriteFile(PathOf(titleId, "title", key), value);

    /// <summary>Account ids of the title that have a document under <paramref name="key"/>.</summary>
    public IEnumerable<int> AccountsWith(string titleId, string key)
    {
        string dir = Path.Combine(dataDir, "accountdata", Safe(titleId));
        if (!Directory.Exists(dir))
            yield break;
        foreach (string accountDir in Directory.EnumerateDirectories(dir))
            if (int.TryParse(Path.GetFileName(accountDir), out int id) && File.Exists(Path.Combine(accountDir, Safe(key) + ".json")))
                yield return id;
    }

    private string PathOf(string titleId, string scope, string key) =>
        Path.Combine(dataDir, "accountdata", Safe(titleId), scope, Safe(key) + ".json");

    private T? ReadFile<T>(string path) where T : class
    {
        lock (_lock)
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;
    }

    private void WriteFile<T>(string path, T value)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
    }

    private static string Safe(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
