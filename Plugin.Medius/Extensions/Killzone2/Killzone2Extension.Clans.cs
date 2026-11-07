using Plugin.Medius.Lobby;
using Plugin.Medius.Web;
using System.Text;

namespace Plugin.Medius.Extensions.Killzone2;

/// <summary>
/// Clans. The online menu (disc menu/online/menu.jsp) offers CREATE CLAN (clanCreateURI, once
/// <c>gPlayerProfile:GetMPScore() &gt;= ranks:GetScoreForRank(2)</c>) while the profile's clan id is
/// 0, else MY CLAN (clanURI); the game itself only asks the lobby for a clan's members (lobby 0xDA)
/// and keeps its clan id and tag in the profile (SetClanID / SetClanTag, a 32-byte string at +0xad4,
/// 1.29 0x82d48) [C]. So clans live on the server (<see cref="ClanStore"/>) and both entries open one
/// page, which acts on <c>?do=</c> (create, invite, accept, decline, remove, leave, disband) and then
/// draws the player's clan, or the invitations waiting for them, and writes the clan into the profile.
/// </summary>
public partial class Killzone2Extension
{
    /// <summary>The title's clans; none without a Medius context (tests set one).</summary>
    public ClanStore? Clans { get; set; }

    /// <summary>The server's accounts, for names; none without a Medius context (tests set one).</summary>
    public AccountStore? Accounts { get; set; }

    private MediusWebResponse? ClanPage(MediusWebRequest request)
    {
        Killzone2ClanSettings settings = _settings.Svo.Clans;
        if (Clans is null || AccountOf(request) is not { } me)
        {
            Log.Warning("[medius/{Extension}] {Request}: clan page without a session", Name, request);
            return null;
        }
        if (TemplateFile(settings.Template) is not { } file)
        {
            Log.Warning("[medius/{Extension}] {Request}: no clan template {Template}", Name, request, settings.Template);
            return null;
        }
        string message = ClanAction(request, me);
        string text = File.ReadAllText(file)
            .Replace("{ClanData}", ClanData(me), StringComparison.Ordinal)
            .Replace("{ClanInvites}", ClanInvites(me), StringComparison.Ordinal)
            .Replace("{ClanMessage.lua}", LuaString(message), StringComparison.Ordinal)
            // A name entered but no clan made yet: the page reloads itself with ?name= because the game opens only
            // one keyboard per page (a second ActivateVirtualKeyboard opened nothing in RPCS3 [live 2026-10-31]).
            .Replace("{ClanPendingName.lua}", LuaString(request.QueryString("do") is null && request.QueryString("name") is { } pending
                && settings.Rules.NameFits(pending.Trim()) ? pending.Trim() : ""), StringComparison.Ordinal)
            .Replace("{ClanUrl.lua}", LuaString(_settings.Svo.Uris.FirstOrDefault(u => u.Name == settings.Uris[0]) is { } uri ? UrlOf(uri.Value) : ""), StringComparison.Ordinal)
            .Replace("{ClanNameMax}", settings.Rules.NameMaxLength.ToString(), StringComparison.Ordinal)
            .Replace("{ClanTagMax}", settings.Rules.TagMaxLength.ToString(), StringComparison.Ordinal);
        return new MediusWebResponse
        {
            ContentType = PageTypes[0].ContentType,
            Body = Encoding.UTF8.GetBytes(FillPage(FillValues(text, settings.Values), me)),
        };
    }

    /// <summary>Does what the query asks and says how it went (empty when it asks nothing).</summary>
    private string ClanAction(MediusWebRequest request, int me)
    {
        Killzone2ClanSettings settings = _settings.Svo.Clans;
        string titleId = _title.Descriptor.TitleId;
        string action = request.QueryString("do") ?? "";
        string Arg(string name) => request.QueryString(name) ?? "";
        int Id(string name) => (int)(request.QueryInt64(name) ?? 0);
        (ClanResult result, string subject) = action switch
        {
            "" => (ClanResult.Done, ""),
            "create" => (Clans!.Create(titleId, me, Arg("name"), Arg("tag"), settings.Rules, out _), Arg("name").Trim()),
            "invite" => Accounts?.Find(titleId, Arg("name").Trim()) is { } player
                ? (Clans!.Invite(titleId, me, player.AccountId), player.Name)
                : (ClanResult.PlayerNotFound, Arg("name").Trim()),
            "accept" => (Clans!.Answer(titleId, me, Id("clan"), accept: true), Clans.Find(titleId, Id("clan"))?.Name ?? ""),
            "decline" => (Clans!.Answer(titleId, me, Id("clan"), accept: false), Clans.Find(titleId, Id("clan"))?.Name ?? ""),
            "remove" => (Clans!.Remove(titleId, me, Id("member")), NameOf(Id("member")) ?? ""),
            "leave" => (Clans!.Leave(titleId, me), ""),
            "disband" => (Clans!.Disband(titleId, me), ""),
            _ => (ClanResult.NotMember, ""),
        };
        if (action.Length == 0)
            return "";
        string key = result == ClanResult.Done ? action : result.ToString();
        Log.Information("[medius/{Extension}] {Request}: clan {Action} by #{Account}: {Result} {Subject}", Name, request, action, me, result, subject);
        return string.Format(settings.Messages.GetValueOrDefault(key) ?? key, subject.ToUpperInvariant());
    }

    /// <summary>
    /// The player's clan as a Lua table, or <c>nil</c>: <c>{ id, name, tag, leader = 0|1, members = { { id, name,
    /// online = 0|1|2 (offline, on, in a game), score, leader, me }, ... }, invited = { "name", ... } }</c>,
    /// leader first, then by score.
    /// </summary>
    public string ClanData(int me)
    {
        if (Clans?.OfMember(_title.Descriptor.TitleId, me) is not { } clan)
            return "nil";
        StringBuilder lua = new($"{{ id = {clan.Id}, name = {LuaString(clan.Name)}, tag = {LuaString(clan.Tag)}, leader = {(clan.LeaderAccountId == me ? 1 : 0)}, members = {{ ");
        IEnumerable<(int Id, string Name, int Score)> members = clan.Members
            .Select(id => (Id: id, Name: NameOf(id) ?? $"#{id}", Score: Profiles?.Find(id)?.MPScore ?? 0))
            .OrderByDescending(m => m.Id == clan.LeaderAccountId).ThenByDescending(m => m.Score).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase);
        foreach ((int id, string name, int score) in members)
        {
            MediusClient? online = _context?.Lobby.LoggedIn(_title, name);
            int status = online is null ? 0 : online.Game is null ? 1 : 2;
            lua.Append($"{{ id = {id}, name = {LuaString(name)}, online = {status}, score = {score}, leader = {(id == clan.LeaderAccountId ? 1 : 0)}, me = {(id == me ? 1 : 0)} }}, ");
        }
        lua.Append("}, invited = { ");
        foreach (int id in clan.Invited)
            lua.Append(LuaString(NameOf(id) ?? $"#{id}")).Append(", ");
        return lua.Append("} }").ToString();
    }

    /// <summary>The clans that invited the player: <c>{ { id, name, tag }, ... }</c>.</summary>
    public string ClanInvites(int me)
    {
        StringBuilder lua = new("{ ");
        foreach (MediusClan clan in Clans?.InvitationsOf(_title.Descriptor.TitleId, me) ?? [])
            lua.Append($"{{ id = {clan.Id}, name = {LuaString(clan.Name)}, tag = {LuaString(clan.Tag)} }}, ");
        return lua.Append('}').ToString();
    }

    /// <summary>{ProfileClanId} and {ProfileClanTag.lua}: the clan the profile should hold (0 and "" for none).</summary>
    public static string FillClan(string text, MediusClan? clan) => text
        .Replace("{ProfileClanId}", (clan?.Id ?? 0).ToString(), StringComparison.Ordinal)
        .Replace("{ProfileClanTag.lua}", LuaString(clan?.Tag ?? ""), StringComparison.Ordinal);

    /// <summary>Clan board rows: clans by their members' total MPScore, named "Name [TAG]".</summary>
    private void ClanRows(StringBuilder lua, Killzone2BoardSettings board, int? me)
    {
        int position = 0;
        var clans = (Clans?.All(_title.Descriptor.TitleId) ?? [])
            .Select(c => (Clan: c, Score: c.Members.Sum(id => Profiles?.Find(id)?.MPScore ?? 0)))
            .OrderByDescending(c => c.Score).ThenBy(c => c.Clan.Name, StringComparer.OrdinalIgnoreCase)
            .Take(board.MaxRows);
        foreach ((MediusClan clan, int score) in clans)
            lua.Append($"{{ pos = {++position}, name = {LuaString($"{clan.Name} [{clan.Tag}]")}, score = {score}, rank = 0, me = {(me is { } id && clan.Members.Contains(id) ? 1 : 0)} }}, ");
    }

    private string? NameOf(int accountId) => Accounts?.Find(accountId)?.Name;
}

/// <summary>The clan page (see Killzone2Extension.ClanPage).</summary>
public sealed class Killzone2ClanSettings
{
    /// <summary>The named URIs that open it (clanCreateURI and clanURI share it).</summary>
    public List<string> Uris { get; set; } = ["clanURI", "clanCreateURI"];
    /// <summary>The template file under TemplatesDir.</summary>
    public string Template { get; set; } = "clan.svml";
    /// <summary>The template's {Name} / {Name.lua} values (titles and texts).</summary>
    public Dictionary<string, string> Values { get; set; } = [];
    public ClanRules Rules { get; set; } = new();
    /// <summary>
    /// What the page says after an action, by the action (create, invite, accept, decline, remove, leave,
    /// disband) when it worked, else by the <see cref="ClanResult"/>; {0} is the clan or player named.
    /// </summary>
    public Dictionary<string, string> Messages { get; set; } = new()
    {
        ["create"] = "CLAN {0} CREATED",
        ["invite"] = "{0} HAS BEEN INVITED",
        ["accept"] = "YOU JOINED {0}",
        ["decline"] = "INVITATION FROM {0} DECLINED",
        ["remove"] = "{0} WAS REMOVED FROM THE CLAN",
        ["leave"] = "YOU LEFT THE CLAN",
        ["disband"] = "THE CLAN WAS DISBANDED",
        [nameof(ClanResult.NameInvalid)] = "THAT CLAN NAME CANNOT BE USED",
        [nameof(ClanResult.TagInvalid)] = "THAT CLAN TAG CANNOT BE USED",
        [nameof(ClanResult.NameTaken)] = "CLAN NAME {0} IS TAKEN",
        [nameof(ClanResult.TagTaken)] = "THAT CLAN TAG IS TAKEN",
        [nameof(ClanResult.AlreadyInClan)] = "ALREADY IN A CLAN",
        [nameof(ClanResult.NotInClan)] = "YOU ARE NOT IN A CLAN",
        [nameof(ClanResult.NotLeader)] = "ONLY THE CLAN LEADER CAN DO THAT",
        [nameof(ClanResult.NotInvited)] = "THAT INVITATION IS NO LONGER OPEN",
        [nameof(ClanResult.PlayerNotFound)] = "NO PLAYER CALLED {0}",
        [nameof(ClanResult.NotMember)] = "THAT PLAYER IS NOT A MEMBER",
    };
}
