using System.Text;
using System.Text.Json;
using Plugin.Medius.Extensions.Killzone2;
using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Web;
using ServerShared.Titles;
using Player = Plugin.Medius.Tests.GameFlowTests.Player;

namespace Plugin.Medius.Tests;

/// <summary>
/// Clans: the store's rules, Killzone 2's clan page (clanURI / clanCreateURI with ?do=...), the clan
/// boards, and the lobby's clan member list (lobby 0xDA -> 0xDB, Killzone 2 1.29 codecs 0x72205c /
/// 0x722078).
/// </summary>
[Collection(MediusServerCollection.Name)]
public sealed class ClanTests(LobbyFlowFixture fixture)
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "kz2-clans-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Store_keeps_one_clan_per_player_and_hands_over_leadership()
    {
        string dir = TempDir();
        try
        {
            ClanStore clans = new(new AccountDataStore(dir));
            ClanRules rules = new();
            Assert.Equal(ClanResult.NameInvalid, clans.Create("t", 1, "x", "AB", rules, out _));
            Assert.Equal(ClanResult.NameInvalid, clans.Create("t", 1, "Bad&Name", "AB", rules, out _));
            Assert.Equal(ClanResult.TagInvalid, clans.Create("t", 1, "Helghast", "TOOLONG", rules, out _));
            Assert.Equal(ClanResult.Done, clans.Create("t", 1, " Helghast Army ", "HGA", rules, out MediusClan? clan));
            Assert.Equal(("Helghast Army", "HGA", 1), (clan!.Name, clan.Tag, clan.LeaderAccountId));
            Assert.Equal(ClanResult.NameTaken, clans.Create("t", 2, "helghast army", "XX", rules, out _));
            Assert.Equal(ClanResult.TagTaken, clans.Create("t", 2, "Other", "hga", rules, out _));
            Assert.Equal(ClanResult.AlreadyInClan, clans.Create("t", 1, "Second", "SEC", rules, out _));

            Assert.Equal(ClanResult.NotInClan, clans.Invite("t", 2, 3));
            Assert.Equal(ClanResult.Done, clans.Invite("t", 1, 2));
            Assert.Equal(ClanResult.Done, clans.Invite("t", 1, 3));
            Assert.Equal(clan.Id, Assert.Single(clans.InvitationsOf("t", 2)).Id);
            Assert.Equal(ClanResult.Done, clans.Answer("t", 2, clan.Id, accept: true));
            Assert.Equal(ClanResult.Done, clans.Answer("t", 3, clan.Id, accept: false));
            Assert.Equal(ClanResult.NotInvited, clans.Answer("t", 3, clan.Id, accept: true));
            Assert.Equal([1, 2], clans.OfMember("t", 2)!.Members);

            Assert.Equal(ClanResult.NotLeader, clans.Remove("t", 2, 1));
            Assert.Equal(ClanResult.Done, clans.Leave("t", 1));
            Assert.Equal(2, clans.Find("t", clan.Id)!.LeaderAccountId);
            Assert.Equal(ClanResult.Done, clans.Leave("t", 2));
            Assert.Empty(clans.All("t"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Clan_page_creates_invites_joins_and_ranks()
    {
        string dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "medius", "svo-templates"));
        File.WriteAllText(Path.Combine(dir, "medius", "svo-templates", "clan.svml"),
            "<SVML><_s><![CDATA[{ClanData}|{ClanInvites}|{ClanMessage.lua}|{ProfileClanId}|{ProfileClanTag.lua}|{Note}|{ClanPendingName.lua}]]></_s></SVML>");
        File.WriteAllText(Path.Combine(dir, "medius", "svo-templates", "board.svml"), "<SVML>{BoardRows}</SVML>");
        try
        {
            Killzone2Extension extension = new();
            extension.Initialize(new MediusTitle
            {
                Descriptor = new TitleDescriptor { TitleId = "killzone2", ResourceDir = dir },
                ExtensionSettings = JsonDocument.Parse("""
                    { "Svo": { "Hosts": [ "svo.test" ], "BaseUrl": "http://svo.test", "TemplatesDir": "svo-templates",
                               "Uris": [ { "Name": "clanURI", "Value": "/killzone2/clans" }, { "Name": "clanCreateURI", "Value": "/killzone2/clans" },
                                         { "Name": "leaderboardClanURI", "Value": "/killzone2/clans/leaderboards" },
                                         { "Name": "leaderboardClanMembersURI", "Value": "/killzone2/clans/leaderboards/members" } ],
                               "Boards": [ { "Uri": "leaderboardClanURI", "Template": "board.svml", "Source": "Clans" },
                                           { "Uri": "leaderboardClanMembersURI", "Template": "board.svml", "Source": "ClanMembers" } ],
                               "Clans": { "Values": { "Note": "n" } } } }
                    """).RootElement,
            }, null!);
            AccountStore accounts = new(dir);
            int leaderId = accounts.Create("killzone2", "dyllan501", null)!.AccountId;
            int joinerId = accounts.Create("killzone2", "dyllan500", null)!.AccountId;
            extension.Accounts = accounts;
            extension.Clans = new ClanStore(new AccountDataStore(dir));
            extension.Profiles = new Killzone2Profiles(new AccountDataStore(dir), "killzone2", id => accounts.Find(id)?.Name);
            extension.Profiles.Save(leaderId, new Dictionary<string, string> { ["MPScore"] = "300" });
            extension.Profiles.Save(joinerId, new Dictionary<string, string> { ["MPScore"] = "500" });
            string leader = extension.StartSession(leaderId), joiner = extension.StartSession(joinerId);
            string Get(string target, string session) => Encoding.UTF8.GetString(extension.HandleWeb(new MediusWebRequest
            {
                Method = "GET", Target = target, Headers = new Dictionary<string, string> { ["host"] = "svo.test", ["cookie"] = $"AuthKey={session}" },
            })!.Body);

            Assert.Equal("<SVML><_s><![CDATA[nil|{ }|\"\"|0|\"\"|n|\"\"]]></_s></SVML>", Get("/killzone2/clans", leader));
            // The name step reloads the page with ?name=; a name the rules refuse is dropped.
            Assert.EndsWith("|n|\"Helghast Army\"]]></_s></SVML>", Get("/killzone2/clans?name=Helghast%20Army", leader));
            Assert.EndsWith("|n|\"\"]]></_s></SVML>", Get("/killzone2/clans?name=x", leader));
            string created = Get("/killzone2/clans?do=create&name=Helghast%20Army&tag=HGA", leader);
            Assert.Contains("{ id = 1, name = \"Helghast Army\", tag = \"HGA\", leader = 1, members = { { id = " + leaderId + ", name = \"dyllan501\", online = 0, score = 300, leader = 1, me = 1 }, }, invited = { } }", created);
            Assert.Contains("|\"CLAN HELGHAST ARMY CREATED\"|1|\"HGA\"|", created);

            Assert.Contains("\"NO PLAYER CALLED NOBODY\"", Get("/killzone2/clans?do=invite&name=nobody", leader));
            Assert.Contains("invited = { \"dyllan500\", }", Get("/killzone2/clans?do=invite&name=dyllan500", leader));
            Assert.Equal("<SVML><_s><![CDATA[nil|{ { id = 1, name = \"Helghast Army\", tag = \"HGA\" }, }|\"\"|0|\"\"|n|\"\"]]></_s></SVML>", Get("/killzone2/clans", joiner));
            string joined = Get("/killzone2/clans?do=accept&clan=1", joiner);
            Assert.Contains("\"YOU JOINED HELGHAST ARMY\"|1|\"HGA\"|", joined);
            // Leader first, then by score.
            Assert.Contains("members = { { id = " + leaderId + ", name = \"dyllan501\"", joined);

            Assert.Equal("<SVML>{ { pos = 1, name = \"Helghast Army \\091HGA\\093\", score = 800, rank = 0, me = 1 }, }</SVML>",
                Get("/killzone2/clans/leaderboards", joiner));
            Assert.Equal("<SVML>{ { pos = 1, name = \"dyllan500\", score = 500, rank = 1, me = 1 }, { pos = 2, name = \"dyllan501\", score = 300, rank = 1, me = 0 }, }</SVML>",
                Get("/killzone2/clans/leaderboards/members", joiner));

            Assert.Contains("\"ONLY THE CLAN LEADER CAN DO THAT\"", Get("/killzone2/clans?do=disband", joiner));
            Assert.Contains("|\"YOU LEFT THE CLAN\"|0|\"\"|", Get("/killzone2/clans?do=leave", joiner));
            Assert.Contains("|\"THE CLAN WAS DISBANDED\"|0|\"\"|", Get("/killzone2/clans?do=disband", leader));
            Assert.Equal("<SVML>{ }</SVML>", Get("/killzone2/clans/leaderboards", leader));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Lobby_lists_a_clans_members()
    {
        await using Player leader = await Player.LoginAsync(fixture, "kz-clan-a");
        await using Player member = await Player.LoginAsync(fixture, "kz-clan-b");
        MediusContext context = fixture.Plugin.Context!;
        MediusTitle title = context.Titles.First(t => t.AppIds.Contains(LobbyFlowFixture.AppId));
        string titleId = title.Descriptor.TitleId;
        int a = context.Lobby.Accounts.Find(titleId, "kz-clan-a")!.AccountId, b = context.Lobby.Accounts.Find(titleId, "kz-clan-b")!.AccountId;
        Assert.Equal(ClanResult.Done, context.Clans.Create(titleId, a, "Clan Test " + a, "T" + a % 1000, new ClanRules { TagMaxLength = 8 }, out MediusClan? clan));
        context.Clans.Invite(titleId, a, b);
        context.Clans.Answer(titleId, b, clan!.Id, accept: true);

        member.Mls.SendApp(new MediusGetClanMemberList_ExtraInfoRequest { MessageID = new MessageId("cm"), ClanID = clan.Id });
        List<MediusGetClanMemberList_ExtraInfoResponse> rows = [];
        do
            rows.Add(await member.Mls.ExpectAppAsync<MediusGetClanMemberList_ExtraInfoResponse>());
        while (!rows[^1].EndOfList);
        Assert.Equal(["kz-clan-a", "kz-clan-b"], rows.Select(r => r.AccountName));
        Assert.All(rows, r => Assert.Equal(MediusPlayerStatus.MediusPlayerInChatWorld, r.OnlineState.ConnectStatus));

        member.Mls.SendApp(new MediusGetClanMemberList_ExtraInfoRequest { MessageID = new MessageId("cx"), ClanID = 99999 });
        Assert.Equal(MediusCallbackStatus.MediusNoResult, (await member.Mls.ExpectAppAsync<MediusGetClanMemberList_ExtraInfoResponse>()).StatusCode);
        context.Clans.Disband(titleId, a);
    }
}
