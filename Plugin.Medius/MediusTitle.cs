using ServerShared.Titles;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plugin.Medius;

/// <summary>
/// One Medius game, read from <c>&lt;ResourceDir&gt;/medius/title.json</c> of a
/// title whose <c>ProtocolFamily</c> is <c>medius</c>. A connection is matched
/// to its title by the application id in its RT connect message, so every
/// regional id the game uses is listed here.
/// </summary>
public sealed class MediusTitle
{
    public const string ProtocolFamily = "medius";
    public const string FileName = "medius/title.json";

    /// <summary>The <see cref="TitleDescriptor"/> this was loaded for; not part of the file.</summary>
    [JsonIgnore]
    public TitleDescriptor Descriptor { get; set; } = new();

    /// <summary>Every Medius application id the game connects with (one per region, usually).</summary>
    public List<int> AppIds { get; set; } = [];

    /// <summary>RC/RSA-encrypt RT messages. Off is easier to debug, but some clients insist on it.</summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>What the MUIS answers. Empty means one universe pointing at this server's MAS.</summary>
    public List<MediusUniverse> Universes { get; set; } = [];

    /// <summary>Lobby channels created at start. Empty means one channel named after the game.</summary>
    public List<MediusChannelDefinition> Channels { get; set; } = [];

    /// <summary>Text for MediusGetPolicy (usage and privacy policy screens).</summary>
    public string Policy { get; set; } = "";

    /// <summary>Announcements, newest first.</summary>
    public List<string> Announcements { get; set; } = [];

    /// <summary>News shown by the MUIS when the client asks for it.</summary>
    public string News { get; set; } = "";

    /// <summary>
    /// Log in names the server has never seen by creating their account,
    /// rather than refusing. PS3 ticket logins always create.
    /// </summary>
    public bool CreateAccountOnLogin { get; set; } = true;

    /// <summary>
    /// The title extension to load (e.g. <c>mag</c>), for games whose
    /// developer added services of their own beside stock Medius. Empty for
    /// stock games.
    /// </summary>
    public string Extension { get; set; } = "";

    /// <summary>Settings only the extension reads, passed through untouched.</summary>
    public JsonElement ExtensionSettings { get; set; }

    public override string ToString() => Descriptor.ToString();

    /// <summary>
    /// Loads the Medius half of every <c>medius</c> title. A title whose file
    /// is missing or unreadable is reported and skipped.
    /// </summary>
    public static List<MediusTitle> LoadAll(IEnumerable<TitleDescriptor> titles, List<string> problems)
    {
        List<MediusTitle> loaded = [];
        foreach (TitleDescriptor descriptor in titles.Where(t =>
                     string.Equals(t.ProtocolFamily, ProtocolFamily, StringComparison.OrdinalIgnoreCase)))
        {
            string path = Path.Combine(descriptor.ResourceDir, FileName);
            if (!File.Exists(path))
            {
                problems.Add($"{descriptor}: {path} not found");
                continue;
            }

            try
            {
                MediusTitle? title = JsonSerializer.Deserialize<MediusTitle>(File.ReadAllText(path), JsonOptions);
                if (title is null || title.AppIds.Count == 0)
                {
                    problems.Add($"{descriptor}: {path} lists no AppIds");
                    continue;
                }
                title.Descriptor = descriptor;
                loaded.Add(title);
            }
            catch (JsonException exception)
            {
                problems.Add($"{descriptor}: {path} is not valid: {exception.Message}");
            }
        }

        foreach (var duplicate in loaded.SelectMany(t => t.AppIds.Select(id => (id, t))).GroupBy(x => x.id).Where(g => g.Count() > 1))
            problems.Add($"app id {duplicate.Key} is claimed by {string.Join(" and ", duplicate.Select(x => x.t))}");

        return loaded;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public sealed class MediusUniverse
{
    public uint Id { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>The MAS host clients are sent to; empty means this server's PublicHost.</summary>
    public string Host { get; set; } = "";
    /// <summary>The MAS port; 0 means this server's MAS listener.</summary>
    public int Port { get; set; }
    public string SvoUrl { get; set; } = "";
    public string ExtendedInfo { get; set; } = "";
}

public sealed class MediusChannelDefinition
{
    public string Name { get; set; } = "";
    public int MaxPlayers { get; set; } = 256;
}
