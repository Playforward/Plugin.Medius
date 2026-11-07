using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;
using Player = Plugin.Medius.Tests.GameFlowTests.Player;

namespace Plugin.Medius.Tests;

/// <summary>
/// Buddy lists and invitations on the MLS, as Killzone 2 1.29's friends page
/// drives them: AddBuddy (lobby-ext 0x63 -> 0x64), the target's forwarded
/// invitation (0x06) and answer (0x07), GetBuddyInvitations (0x08 -> 0x09),
/// GetBuddyList_ExtraInfo (lobby 0xD6 -> 0xD7) and RemoveBuddy (0x41 -> 0x42).
/// Sizes are the client's codecs (registration 0x721030..0x72231c).
/// </summary>
[Collection(MediusServerCollection.Name)]
public sealed class BuddyTests(LobbyFlowFixture fixture)
{
    private const int Ps3 = 113;
    private const int AppHeader = 2;

    private static byte[] AppBytes(BaseMediusMessage message, bool fromClient)
    {
        using MemoryStream stream = new();
        using (MessageWriter writer = new(stream) { MediusVersion = Ps3 })
        {
            if (fromClient)
                new RT_MSG_CLIENT_APP_TOSERVER { Message = message }.Serialize(writer);
            else
                new RT_MSG_SERVER_APP { Message = message }.Serialize(writer);
        }
        return stream.ToArray();
    }

    private static BaseMediusMessage Read(byte[] bytes, bool fromClient)
    {
        using MessageReader reader = new(new MemoryStream(bytes)) { MediusVersion = Ps3 };
        return BaseMediusMessage.Instantiate(reader, fromClient);
    }

    [Fact]
    public void Sizes_are_the_clients()
    {
        Assert.Equal(AppHeader + 48, AppBytes(new MediusAddToBuddyListConfirmationRequest { SessionKey = "" }, true).Length);
        Assert.Equal(AppHeader + 64, AppBytes(new MediusAddToBuddyListConfirmationResponse { TargetAccountName = "" }, false).Length);
        Assert.Equal(AppHeader + 64, AppBytes(new MediusAddToBuddyListFwdConfirmationRequest { OriginatorAccountName = "" }, false).Length);
        Assert.Equal(AppHeader + 56, AppBytes(new MediusAddToBuddyListFwdConfirmationResponse { SessionKey = "" }, true).Length);
        Assert.Equal(AppHeader + 21, AppBytes(new MediusGetBuddyInvitationsRequest(), true).Length);
        Assert.Equal(AppHeader + 72, AppBytes(new MediusGetBuddyInvitationsResponse { AccountName = "" }, false).Length);
        Assert.Equal(AppHeader + 208, AppBytes(new MediusGetBuddyList_ExtraInfoResponse { AccountName = "", OnlineState = new() { LobbyName = "", GameName = "" } }, false).Length);
    }

    [Fact]
    public void Ignore_list_request_without_the_pad_reads_back()
    {
        // 1.29 writes MessageID and SessionKey only (codec 0x72d878): 38 bytes [C].
        byte[] bytes = [(byte)NetMessageTypes.MessageClassLobby, (byte)MediusLobbyMessageIds.GetIgnoreList, .. new byte[38]];
        "7"u8.CopyTo(bytes.AsSpan(2));
        "1234"u8.CopyTo(bytes.AsSpan(2 + 21));
        MediusGetIgnoreListRequest request = Assert.IsType<MediusGetIgnoreListRequest>(Read(bytes, fromClient: true));
        Assert.Equal("1234", request.SessionKey);
    }

    [Fact]
    public async Task Invitation_is_forwarded_accepted_listed_and_removed()
    {
        await using Player inviter = await Player.LoginAsync(fixture, "kz-buddy-a");
        await using Player target = await Player.LoginAsync(fixture, "kz-buddy-b");
        int inviterId = await AccountIdAsync(target, "kz-buddy-a");
        int targetId = await AccountIdAsync(inviter, "kz-buddy-b");

        inviter.Mls.SendApp(new MediusAddToBuddyListConfirmationRequest
        {
            MessageID = new MessageId("i1"), SessionKey = inviter.SessionKey, TargetAccountID = targetId, AddType = MediusBuddyAddType.AddSymmetric,
        });
        MediusAddToBuddyListConfirmationResponse sent = await inviter.Mls.ExpectAppAsync<MediusAddToBuddyListConfirmationResponse>();
        Assert.Equal((MediusCallbackStatus.MediusSuccess, "i1", targetId, "kz-buddy-b"), (sent.StatusCode, sent.MessageID.Value, sent.TargetAccountID, sent.TargetAccountName));

        MediusAddToBuddyListFwdConfirmationRequest forwarded = await target.Mls.ExpectAppAsync<MediusAddToBuddyListFwdConfirmationRequest>();
        Assert.Equal((inviterId, "kz-buddy-a", MediusBuddyAddType.AddSymmetric), (forwarded.OriginatorAccountID, forwarded.OriginatorAccountName, forwarded.AddType));

        target.Mls.SendApp(new MediusGetBuddyInvitationsRequest { MessageID = new MessageId("g1") });
        MediusGetBuddyInvitationsResponse waiting = await target.Mls.ExpectAppAsync<MediusGetBuddyInvitationsResponse>();
        Assert.Equal((MediusCallbackStatus.MediusSuccess, inviterId, "kz-buddy-a", true), (waiting.StatusCode, waiting.AccountID, waiting.AccountName, waiting.EndOfList));

        // Inviting twice keeps one invitation.
        inviter.Mls.SendApp(new MediusAddToBuddyListConfirmationRequest
        {
            MessageID = new MessageId("i2"), SessionKey = inviter.SessionKey, TargetAccountID = targetId, AddType = MediusBuddyAddType.AddSymmetric,
        });
        await inviter.Mls.ExpectAppAsync<MediusAddToBuddyListConfirmationResponse>();
        await target.Mls.ExpectAppAsync<MediusAddToBuddyListFwdConfirmationRequest>();

        target.Mls.SendApp(new MediusAddToBuddyListFwdConfirmationResponse
        {
            MessageID = new MessageId(""), StatusCode = MediusCallbackStatus.MediusSuccess, SessionKey = target.SessionKey,
            OriginatorAccountID = inviterId, AddType = MediusBuddyAddType.AddSymmetric,
        });
        // The answer has no reply; the invitation list going empty shows it was taken.
        await EventuallyAsync(async () => (await InvitationsAsync(target)).Single().StatusCode == MediusCallbackStatus.MediusNoResult);

        MediusGetBuddyList_ExtraInfoResponse buddy = Assert.Single(await BuddiesAsync(inviter));
        Assert.Equal((MediusCallbackStatus.MediusSuccess, targetId, "kz-buddy-b", MediusPlayerStatus.MediusPlayerInChatWorld, true),
            (buddy.StatusCode, buddy.AccountID, buddy.AccountName, buddy.OnlineState.ConnectStatus, buddy.EndOfList));
        Assert.Equal(inviterId, Assert.Single(await BuddiesAsync(target)).AccountID);

        // Already buddies: no new invitation.
        inviter.Mls.SendApp(new MediusAddToBuddyListConfirmationRequest
        {
            MessageID = new MessageId("i3"), SessionKey = inviter.SessionKey, TargetAccountID = targetId, AddType = MediusBuddyAddType.AddSymmetric,
        });
        Assert.Equal(MediusCallbackStatus.MediusDataAlreadyExists, (await inviter.Mls.ExpectAppAsync<MediusAddToBuddyListConfirmationResponse>()).StatusCode);

        inviter.Mls.SendApp(new MediusRemoveFromBuddyListRequest { MessageID = new MessageId("r1"), SessionKey = inviter.SessionKey, AccountID = targetId });
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await inviter.Mls.ExpectAppAsync<MediusRemoveFromBuddyListResponse>()).StatusCode);
        MediusGetBuddyList_ExtraInfoResponse none = Assert.Single(await BuddiesAsync(inviter));
        Assert.Equal((MediusCallbackStatus.MediusNoResult, true), (none.StatusCode, none.EndOfList));
    }

    [Fact]
    public async Task Declined_and_ignored_invitations_add_nobody()
    {
        await using Player inviter = await Player.LoginAsync(fixture, "kz-buddy-c");
        await using Player target = await Player.LoginAsync(fixture, "kz-buddy-d");
        int inviterId = await AccountIdAsync(target, "kz-buddy-c");
        int targetId = await AccountIdAsync(inviter, "kz-buddy-d");

        // Inviting oneself is denied.
        inviter.Mls.SendApp(new MediusAddToBuddyListConfirmationRequest { MessageID = new MessageId("s"), SessionKey = inviter.SessionKey, TargetAccountID = inviterId });
        Assert.Equal(MediusCallbackStatus.MediusRequestDenied, (await inviter.Mls.ExpectAppAsync<MediusAddToBuddyListConfirmationResponse>()).StatusCode);

        inviter.Mls.SendApp(new MediusAddToBuddyListConfirmationRequest { MessageID = new MessageId("d"), SessionKey = inviter.SessionKey, TargetAccountID = targetId });
        await inviter.Mls.ExpectAppAsync<MediusAddToBuddyListConfirmationResponse>();
        await target.Mls.ExpectAppAsync<MediusAddToBuddyListFwdConfirmationRequest>();
        target.Mls.SendApp(new MediusAddToBuddyListFwdConfirmationResponse
        {
            MessageID = new MessageId(""), StatusCode = MediusCallbackStatus.MediusRequestDenied, SessionKey = target.SessionKey, OriginatorAccountID = inviterId,
        });
        await EventuallyAsync(async () => (await InvitationsAsync(target)).Single().StatusCode == MediusCallbackStatus.MediusNoResult);
        Assert.Equal(MediusCallbackStatus.MediusNoResult, Assert.Single(await BuddiesAsync(inviter)).StatusCode);

        // A player who ignores the inviter is not sent the invitation.
        target.Mls.SendApp(new MediusAddToIgnoreListRequest { MessageID = new MessageId("ig"), SessionKey = target.SessionKey, IgnoreAccountID = inviterId });
        Assert.Equal(MediusCallbackStatus.MediusSuccess, (await target.Mls.ExpectAppAsync<MediusAddToIgnoreListResponse>()).StatusCode);
        target.Mls.SendApp(new MediusGetIgnoreListRequest { MessageID = new MessageId("gl"), SessionKey = target.SessionKey });
        MediusGetIgnoreListResponse ignored = await target.Mls.ExpectAppAsync<MediusGetIgnoreListResponse>();
        Assert.Equal((inviterId, "kz-buddy-c", true), (ignored.IgnoreAccountID, ignored.IgnoreAccountName, ignored.EndOfList));
        inviter.Mls.SendApp(new MediusAddToBuddyListConfirmationRequest { MessageID = new MessageId("x"), SessionKey = inviter.SessionKey, TargetAccountID = targetId });
        await inviter.Mls.ExpectAppAsync<MediusAddToBuddyListConfirmationResponse>();
        Assert.Equal(MediusCallbackStatus.MediusNoResult, Assert.Single(await InvitationsAsync(target)).StatusCode);
    }

    [Fact]
    public async Task Find_player_by_name_and_id()
    {
        await using Player seeker = await Player.LoginAsync(fixture, "kz-buddy-e");
        await using Player sought = await Player.LoginAsync(fixture, "kz-buddy-f");
        seeker.Mls.SendApp(new MediusFindPlayerRequest { MessageID = new MessageId("f1"), SessionKey = seeker.SessionKey, SearchType = MediusPlayerSearchType.PlayerAccountName, Name = "KZ-BUDDY-F" });
        MediusFindPlayerResponse byName = await seeker.Mls.ExpectAppAsync<MediusFindPlayerResponse>();
        Assert.Equal((MediusCallbackStatus.MediusSuccess, "kz-buddy-f", MediusApplicationType.LobbyChatChannel, true),
            (byName.StatusCode, byName.AccountName, byName.ApplicationType, byName.EndOfList));
        seeker.Mls.SendApp(new MediusFindPlayerRequest { MessageID = new MessageId("f2"), SessionKey = seeker.SessionKey, SearchType = MediusPlayerSearchType.PlayerAccountID, ID = byName.AccountID, Name = "" });
        Assert.Equal("kz-buddy-f", (await seeker.Mls.ExpectAppAsync<MediusFindPlayerResponse>()).AccountName);
        seeker.Mls.SendApp(new MediusFindPlayerRequest { MessageID = new MessageId("f3"), SessionKey = seeker.SessionKey, SearchType = MediusPlayerSearchType.PlayerAccountName, Name = "nobody-here" });
        Assert.Equal(MediusCallbackStatus.MediusNoResult, (await seeker.Mls.ExpectAppAsync<MediusFindPlayerResponse>()).StatusCode);
    }

    private static async Task<int> AccountIdAsync(Player asker, string name)
    {
        asker.Mls.SendApp(new MediusAccountGetIDRequest { MessageID = new MessageId("id"), SessionKey = asker.SessionKey, AccountName = name });
        MediusAccountGetIDResponse id = await asker.Mls.ExpectAppAsync<MediusAccountGetIDResponse>();
        Assert.Equal(MediusCallbackStatus.MediusSuccess, id.StatusCode);
        return id.AccountID;
    }

    private static async Task<List<MediusGetBuddyInvitationsResponse>> InvitationsAsync(Player player)
    {
        player.Mls.SendApp(new MediusGetBuddyInvitationsRequest { MessageID = new MessageId("inv") });
        List<MediusGetBuddyInvitationsResponse> all = [];
        do
            all.Add(await player.Mls.ExpectAppAsync<MediusGetBuddyInvitationsResponse>());
        while (!all[^1].EndOfList);
        return all;
    }

    private static async Task<List<MediusGetBuddyList_ExtraInfoResponse>> BuddiesAsync(Player player)
    {
        player.Mls.SendApp(new MediusGetBuddyList_ExtraInfoRequest { MessageID = new MessageId("bl") });
        List<MediusGetBuddyList_ExtraInfoResponse> all = [];
        do
            all.Add(await player.Mls.ExpectAppAsync<MediusGetBuddyList_ExtraInfoResponse>());
        while (!all[^1].EndOfList);
        return all;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (int i = 0; i < 40; i++)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }
        Assert.Fail("condition never held");
    }
}
