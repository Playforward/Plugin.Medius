using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Components;

/// <summary>
/// Buddy and ignore lists, kept per account in <see cref="MediusContext.Buddies"/>.
/// </summary>
/// <remarks>
/// Invitations (Killzone 2 1.29's AddBuddy, lobby-ext 0x63): the inviter gets
/// one AddToBuddyListConfirmationResponse at once, since its client completes
/// the request by MessageID (0x7297b0) [C]; the invitation waits on the
/// target's account, is forwarded to the target if online (lobby-ext 0x06),
/// and is listed by GetBuddyInvitations (0x08). The target's
/// FwdConfirmationResponse (0x07, sent without a callback) accepts or declines:
/// accepting puts the target on the inviter's list, and with AddSymmetric the
/// inviter on the target's too.
/// </remarks>
public sealed partial class MlsComponent
{
    private bool HandleBuddies(ScertConnection connection, MediusClient client, BaseMediusMessage message)
    {
        string titleId = client.Title.Descriptor.TitleId;
        BuddyStore store = Context.Buddies;
        switch (message)
        {
            case MediusGetBuddyListRequest request:
            {
                // The plain list (lobby 0x3D/0x3E; Resistance: Fall of Man) [C].
                List<MediusAccount> buddies = AccountsOf(client, store.Get(titleId, client.AccountId).Buddies);
                if (buddies.Count == 0)
                    connection.SendApp(new MediusGetBuddyListResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
                for (int i = 0; i < buddies.Count; i++)
                {
                    MediusClient? online = Context.Lobby.LoggedIn(client.Title, buddies[i].Name);
                    connection.SendApp(new MediusGetBuddyListResponse
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        AccountID = buddies[i].AccountId,
                        AccountName = buddies[i].Name,
                        PlayerStatus = online is null ? MediusPlayerStatus.MediusPlayerDisconnected : OnlineState(online).ConnectStatus,
                        EndOfList = i == buddies.Count - 1,
                    });
                }
                return true;
            }

            case MediusGetBuddyList_ExtraInfoRequest request:
            {
                List<MediusAccount> buddies = AccountsOf(client, store.Get(titleId, client.AccountId).Buddies);
                if (buddies.Count == 0)
                    connection.SendApp(new MediusGetBuddyList_ExtraInfoResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
                for (int i = 0; i < buddies.Count; i++)
                {
                    MediusClient? online = Context.Lobby.LoggedIn(client.Title, buddies[i].Name);
                    connection.SendApp(new MediusGetBuddyList_ExtraInfoResponse
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        AccountID = buddies[i].AccountId,
                        AccountName = buddies[i].Name,
                        OnlineState = online is null ? Offline() : OnlineState(online),
                        EndOfList = i == buddies.Count - 1,
                    });
                }
                return true;
            }

            case MediusAddToBuddyListRequest request:
            {
                MediusCallbackStatus status = TargetStatus(client, request.AccountID, out _);
                if (status == MediusCallbackStatus.MediusSuccess)
                    store.Update(titleId, client.AccountId, lists => AddOnce(lists.Buddies, request.AccountID));
                connection.SendApp(new MediusAddToBuddyListResponse { MessageID = request.MessageID, StatusCode = status });
                return true;
            }

            case MediusRemoveFromBuddyListRequest request:
            {
                bool removed = store.Update(titleId, client.AccountId, lists => lists.Buddies.Remove(request.AccountID));
                Log.Information("{Tag} {Client} removed buddy #{Buddy}: {Removed}", Tag, client, request.AccountID, removed);
                connection.SendApp(new MediusRemoveFromBuddyListResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = removed ? MediusCallbackStatus.MediusSuccess : MediusCallbackStatus.MediusDataDoesNotExist,
                });
                return true;
            }

            case MediusFindPlayerRequest request:
            {
                // By name or account id (Killzone 2 1.29: lobby 0x39, codecs 0x730eb0 / 0x733908 [C]). An offline
                // player is found too, at no world, so name lookups work for players who are not on.
                MediusAccount? found = request.SearchType == MediusPlayerSearchType.PlayerAccountName
                    ? Context.Lobby.Accounts.Find(titleId, request.Name ?? "")
                    : Context.Lobby.Accounts.Find(request.ID);
                if (found is null || found.TitleId != titleId)
                {
                    connection.SendApp(new MediusFindPlayerResponse
                    {
                        MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, ApplicationName = "", AccountName = "", EndOfList = true,
                    });
                    return true;
                }
                MediusClient? online = Context.Lobby.LoggedIn(client.Title, found.Name);
                connection.SendApp(new MediusFindPlayerResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    ApplicationID = online?.AppId ?? connection.AppId,
                    ApplicationName = online?.Game?.Name ?? online?.Channel?.Name ?? "",
                    ApplicationType = online?.Game is not null ? MediusApplicationType.MediusAppTypeGame : MediusApplicationType.LobbyChatChannel,
                    MediusWorldID = online?.Game?.Id ?? online?.Channel?.Id ?? 0,
                    AccountID = found.AccountId,
                    AccountName = found.Name,
                    EndOfList = true,
                });
                return true;
            }

            case MediusAddToBuddyListConfirmationRequest request:
                Invite(connection, client, request);
                return true;

            case MediusAddToBuddyListFwdConfirmationResponse answer:
                AnswerInvitation(client, answer);
                return true;

            case MediusGetBuddyInvitationsRequest request:
            {
                List<(BuddyInvitation Invitation, MediusAccount From)> invitations = store.Get(titleId, client.AccountId).Invitations
                    .Select(i => (i, Context.Lobby.Accounts.Find(i.FromAccountId)))
                    .Where(p => p.Item2 is not null && p.Item2.TitleId == titleId)
                    .Select(p => (p.i, p.Item2!))
                    .ToList();
                if (invitations.Count == 0)
                    connection.SendApp(new MediusGetBuddyInvitationsResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
                for (int i = 0; i < invitations.Count; i++)
                    connection.SendApp(new MediusGetBuddyInvitationsResponse
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        AccountID = invitations[i].From.AccountId,
                        AccountName = invitations[i].From.Name,
                        AddType = invitations[i].Invitation.AddType,
                        EndOfList = i == invitations.Count - 1,
                    });
                return true;
            }

            case MediusGetIgnoreListRequest request:
            {
                List<MediusAccount> ignored = AccountsOf(client, store.Get(titleId, client.AccountId).Ignored);
                if (ignored.Count == 0)
                    connection.SendApp(new MediusGetIgnoreListResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
                for (int i = 0; i < ignored.Count; i++)
                {
                    MediusClient? online = Context.Lobby.LoggedIn(client.Title, ignored[i].Name);
                    connection.SendApp(new MediusGetIgnoreListResponse
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        IgnoreAccountID = ignored[i].AccountId,
                        IgnoreAccountName = ignored[i].Name,
                        PlayerStatus = online is null ? MediusPlayerStatus.MediusPlayerDisconnected : OnlineState(online).ConnectStatus,
                        EndOfList = i == ignored.Count - 1,
                    });
                }
                return true;
            }

            case MediusAddToIgnoreListRequest request:
            {
                MediusCallbackStatus status = TargetStatus(client, request.IgnoreAccountID, out _);
                if (status == MediusCallbackStatus.MediusSuccess)
                    store.Update(titleId, client.AccountId, lists => AddOnce(lists.Ignored, request.IgnoreAccountID));
                connection.SendApp(new MediusAddToIgnoreListResponse { MessageID = request.MessageID, StatusCode = status });
                return true;
            }

            case MediusRemoveFromIgnoreListRequest request:
            {
                bool removed = store.Update(titleId, client.AccountId, lists => lists.Ignored.Remove(request.IgnoreAccountID));
                connection.SendApp(new MediusRemoveFromIgnoreListResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = removed ? MediusCallbackStatus.MediusSuccess : MediusCallbackStatus.MediusDataDoesNotExist,
                });
                return true;
            }
        }
        return false;
    }

    private void Invite(ScertConnection connection, MediusClient client, MediusAddToBuddyListConfirmationRequest request)
    {
        string titleId = client.Title.Descriptor.TitleId;
        MediusCallbackStatus status = TargetStatus(client, request.TargetAccountID, out MediusAccount? target);
        if (status == MediusCallbackStatus.MediusSuccess && Context.Buddies.Get(titleId, client.AccountId).Buddies.Contains(request.TargetAccountID))
            status = MediusCallbackStatus.MediusDataAlreadyExists;
        // A player who ignores the inviter never sees the invitation; the inviter is not told.
        bool ignored = target is not null && Context.Buddies.Get(titleId, target.AccountId).Ignored.Contains(client.AccountId);
        if (status == MediusCallbackStatus.MediusSuccess && target is not null && !ignored)
        {
            Context.Buddies.Update(titleId, target.AccountId, lists =>
            {
                lists.Invitations.RemoveAll(i => i.FromAccountId == client.AccountId);
                lists.Invitations.Add(new BuddyInvitation { FromAccountId = client.AccountId, AddType = request.AddType });
                return true;
            });
            MediusClient? online = Context.Lobby.LoggedIn(client.Title, target.Name);
            online?.Connection?.SendApp(new MediusAddToBuddyListFwdConfirmationRequest
            {
                MessageID = new MessageId(),
                OriginatorAccountID = client.AccountId,
                OriginatorAccountName = client.AccountName,
                AddType = request.AddType,
            });
            Log.Information("{Tag} {Client} invited {Target} to be buddies ({AddType}, {Delivery})",
                Tag, client, $"{target.Name}#{target.AccountId}", request.AddType, online?.Connection is null ? "kept until they look" : "forwarded");
        }
        else
            Log.Information("{Tag} {Client} buddy invitation to #{Target}: {Status}{Ignored}", Tag, client, request.TargetAccountID, status, ignored ? " (ignored by target)" : "");
        connection.SendApp(new MediusAddToBuddyListConfirmationResponse
        {
            MessageID = request.MessageID,
            StatusCode = status,
            TargetAccountID = request.TargetAccountID,
            TargetAccountName = target?.Name ?? "",
        });
    }

    private void AnswerInvitation(MediusClient client, MediusAddToBuddyListFwdConfirmationResponse answer)
    {
        string titleId = client.Title.Descriptor.TitleId;
        BuddyInvitation? invitation = Context.Buddies.Update(titleId, client.AccountId, lists =>
        {
            BuddyInvitation? found = lists.Invitations.FirstOrDefault(i => i.FromAccountId == answer.OriginatorAccountID);
            if (found is not null)
                lists.Invitations.Remove(found);
            return found;
        });
        if (invitation is null)
        {
            Log.Warning("{Tag} {Client} answered a buddy invitation from #{From} that is not waiting", Tag, client, answer.OriginatorAccountID);
            return;
        }
        bool accepted = answer.StatusCode == MediusCallbackStatus.MediusSuccess;
        if (accepted)
        {
            Context.Buddies.Update(titleId, invitation.FromAccountId, lists => AddOnce(lists.Buddies, client.AccountId));
            if (invitation.AddType == MediusBuddyAddType.AddSymmetric)
                Context.Buddies.Update(titleId, client.AccountId, lists => AddOnce(lists.Buddies, invitation.FromAccountId));
        }
        Log.Information("{Tag} {Client} {Answer} the buddy invitation from #{From} ({AddType})",
            Tag, client, accepted ? "accepted" : $"declined ({answer.StatusCode})", invitation.FromAccountId, invitation.AddType);
    }

    /// <summary>Success when <paramref name="accountId"/> is another player of the client's title.</summary>
    private MediusCallbackStatus TargetStatus(MediusClient client, int accountId, out MediusAccount? target)
    {
        target = Context.Lobby.Accounts.Find(accountId);
        if (target is null || target.TitleId != client.Title.Descriptor.TitleId)
            return MediusCallbackStatus.MediusAccountNotFound;
        return accountId == client.AccountId ? MediusCallbackStatus.MediusRequestDenied : MediusCallbackStatus.MediusSuccess;
    }

    private List<MediusAccount> AccountsOf(MediusClient client, IEnumerable<int> ids) =>
        [.. ids.Select(Context.Lobby.Accounts.Find).OfType<MediusAccount>().Where(a => a.TitleId == client.Title.Descriptor.TitleId)];

    private static bool AddOnce(List<int> list, int id)
    {
        if (list.Contains(id))
            return false;
        list.Add(id);
        return true;
    }

    private static MediusPlayerOnlineState Offline() => new() { ConnectStatus = MediusPlayerStatus.MediusPlayerDisconnected, LobbyName = "", GameName = "" };
}
