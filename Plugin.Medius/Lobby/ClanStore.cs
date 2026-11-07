namespace Plugin.Medius.Lobby;

/// <summary>A clan: its leader is always one of its members.</summary>
public sealed class MediusClan
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Tag { get; set; } = "";
    public int LeaderAccountId { get; set; }
    public List<int> Members { get; set; } = [];
    /// <summary>Players invited and not yet answered.</summary>
    public List<int> Invited { get; set; } = [];
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A title's clans, as stored (AccountDataStore title document <see cref="ClanStore.Key"/>).</summary>
public sealed class ClanList
{
    public int NextId { get; set; } = 1;
    public List<MediusClan> Clans { get; set; } = [];
}

/// <summary>What a clan action came to.</summary>
public enum ClanResult
{
    Done,
    NameInvalid,
    TagInvalid,
    NameTaken,
    TagTaken,
    AlreadyInClan,
    NotInClan,
    NotLeader,
    NotInvited,
    PlayerNotFound,
    NotMember,
}

/// <summary>
/// Clans per title: create, invite, answer, remove, leave, disband. A player is
/// in at most one clan; a leader who leaves hands the clan to the longest-serving
/// member, and the last member leaving ends it.
/// </summary>
public sealed class ClanStore(AccountDataStore data)
{
    public const string Key = "clans";

    private readonly object _lock = new();

    public IReadOnlyList<MediusClan> All(string titleId) => Load(titleId).Clans;

    public MediusClan? Find(string titleId, int clanId) => Load(titleId).Clans.FirstOrDefault(c => c.Id == clanId);

    public MediusClan? OfMember(string titleId, int accountId) => Load(titleId).Clans.FirstOrDefault(c => c.Members.Contains(accountId));

    /// <summary>The clans that have invited the player.</summary>
    public IReadOnlyList<MediusClan> InvitationsOf(string titleId, int accountId) =>
        [.. Load(titleId).Clans.Where(c => c.Invited.Contains(accountId))];

    public ClanResult Create(string titleId, int leader, string name, string tag, ClanRules rules, out MediusClan? clan)
    {
        name = name.Trim();
        tag = tag.Trim();
        MediusClan? created = null;
        ClanResult result = Change(titleId, list =>
        {
            if (!rules.NameFits(name))
                return ClanResult.NameInvalid;
            if (!rules.TagFits(tag))
                return ClanResult.TagInvalid;
            if (list.Clans.Any(c => c.Members.Contains(leader)))
                return ClanResult.AlreadyInClan;
            if (list.Clans.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
                return ClanResult.NameTaken;
            // Clans made through the lobby (MediusCreateClanRequest) have no tag; only real tags clash.
            if (tag.Length > 0 && list.Clans.Any(c => string.Equals(c.Tag, tag, StringComparison.OrdinalIgnoreCase)))
                return ClanResult.TagTaken;
            created = new MediusClan { Id = list.NextId++, Name = name, Tag = tag, LeaderAccountId = leader, Members = [leader] };
            list.Clans.Add(created);
            foreach (MediusClan other in list.Clans)
                other.Invited.Remove(leader);
            return ClanResult.Done;
        });
        clan = created;
        return result;
    }

    public ClanResult Invite(string titleId, int leader, int player) => Change(titleId, list =>
    {
        if (list.Clans.FirstOrDefault(c => c.Members.Contains(leader)) is not { } clan)
            return ClanResult.NotInClan;
        if (clan.LeaderAccountId != leader)
            return ClanResult.NotLeader;
        if (list.Clans.Any(c => c.Members.Contains(player)))
            return ClanResult.AlreadyInClan;
        if (!clan.Invited.Contains(player))
            clan.Invited.Add(player);
        return ClanResult.Done;
    });

    /// <summary>Accepts (joins, and drops the player's other invitations) or declines a clan's invitation.</summary>
    public ClanResult Answer(string titleId, int player, int clanId, bool accept) => Change(titleId, list =>
    {
        if (list.Clans.FirstOrDefault(c => c.Id == clanId) is not { } clan || !clan.Invited.Contains(player))
            return ClanResult.NotInvited;
        if (accept && list.Clans.Any(c => c.Members.Contains(player)))
            return ClanResult.AlreadyInClan;
        clan.Invited.Remove(player);
        if (accept)
        {
            clan.Members.Add(player);
            foreach (MediusClan other in list.Clans)
                other.Invited.Remove(player);
        }
        return ClanResult.Done;
    });

    public ClanResult Remove(string titleId, int leader, int member) => Change(titleId, list =>
    {
        if (list.Clans.FirstOrDefault(c => c.Members.Contains(leader)) is not { } clan)
            return ClanResult.NotInClan;
        if (clan.LeaderAccountId != leader)
            return ClanResult.NotLeader;
        if (member == leader || !clan.Members.Remove(member))
            return ClanResult.NotMember;
        return ClanResult.Done;
    });

    public ClanResult Leave(string titleId, int member) => Change(titleId, list =>
    {
        if (list.Clans.FirstOrDefault(c => c.Members.Contains(member)) is not { } clan)
            return ClanResult.NotInClan;
        clan.Members.Remove(member);
        if (clan.Members.Count == 0)
            list.Clans.Remove(clan);
        else if (clan.LeaderAccountId == member)
            clan.LeaderAccountId = clan.Members[0];
        return ClanResult.Done;
    });

    public ClanResult Disband(string titleId, int leader) => Change(titleId, list =>
    {
        if (list.Clans.FirstOrDefault(c => c.Members.Contains(leader)) is not { } clan)
            return ClanResult.NotInClan;
        if (clan.LeaderAccountId != leader)
            return ClanResult.NotLeader;
        list.Clans.Remove(clan);
        return ClanResult.Done;
    });

    private ClanList Load(string titleId) => data.ReadTitle<ClanList>(titleId, Key) ?? new ClanList();

    /// <summary>Runs a change and saves the list when it succeeded.</summary>
    private ClanResult Change(string titleId, Func<ClanList, ClanResult> change)
    {
        lock (_lock)
        {
            ClanList list = Load(titleId);
            ClanResult result = change(list);
            if (result == ClanResult.Done)
                data.WriteTitle(titleId, Key, list);
            return result;
        }
    }
}

/// <summary>What a title allows for clan names and tags.</summary>
public sealed class ClanRules
{
    public int NameMinLength { get; set; } = 3;
    public int NameMaxLength { get; set; } = 31;
    public int TagMinLength { get; set; } = 2;
    public int TagMaxLength { get; set; } = 4;

    /// <summary>Letters, digits, spaces, '-' and '_' (what the pages can send unescaped).</summary>
    public bool NameFits(string name) => name.Length >= NameMinLength && name.Length <= NameMaxLength && name.All(Allowed);

    /// <summary>Letters and digits only.</summary>
    public bool TagFits(string tag) => tag.Length >= TagMinLength && tag.Length <= TagMaxLength && tag.All(char.IsAsciiLetterOrDigit);

    private static bool Allowed(char c) => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_';
}
