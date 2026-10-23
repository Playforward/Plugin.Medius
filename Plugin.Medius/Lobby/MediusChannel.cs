namespace Plugin.Medius.Lobby;

/// <summary>A lobby channel ("world" in Medius terms): a chat room players sit in while browsing games.</summary>
public sealed class MediusChannel(int id, MediusTitle title, string name, int maxPlayers)
{
    private readonly HashSet<MediusClient> _members = [];

    public int Id { get; } = id;
    public MediusTitle Title { get; } = title;
    public string Name { get; } = name;
    public int MaxPlayers { get; } = maxPlayers;

    public int PlayerCount
    {
        get { lock (_members) return _members.Count; }
    }

    public MediusClient[] Members
    {
        get { lock (_members) return [.. _members]; }
    }

    public bool Join(MediusClient client)
    {
        lock (_members)
        {
            if (_members.Count >= MaxPlayers && !_members.Contains(client))
                return false;
            _members.Add(client);
            return true;
        }
    }

    public void Leave(MediusClient client)
    {
        lock (_members)
            _members.Remove(client);
    }

    public override string ToString() => $"{Name}#{Id}";
}
