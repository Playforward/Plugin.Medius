using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Components;

/// <summary>
/// Clans in the lobby, kept in <see cref="MediusContext.Clans"/>. Killzone 2 makes and changes
/// clans through its own pages (the SVO) and reads members with lobby 0xDA (1.29 codecs 0x72205c /
/// 0x722078) [C]; Resistance: Fall of Man creates them here (CreateClan, after TextFilter1 on the
/// name) and polls GetClanMemberList, CheckMyClanInvitations and GetMyClans [live].
/// </summary>
public sealed partial class MlsComponent
{
    /// <summary>Lobby-created clans have only a name: the store's name rules, no tag.</summary>
    private static readonly ClanRules LobbyClanRules = new() { TagMinLength = 0 };

    private bool HandleClans(ScertConnection connection, MediusClient client, BaseMediusMessage message)
    {
        string titleId = client.Title.Descriptor.TitleId;
        switch (message)
        {
            case MediusGetClanMemberList_ExtraInfoRequest request:
                SendClanMembersExtraInfo(connection, client, request);
                return true;

            case MediusGetClanMemberListRequest request:
            {
                List<MediusAccount> members = Context.Clans.Find(titleId, request.ClanID) is { } clan ? AccountsOf(client, clan.Members) : [];
                // The answer has no status field (patch codec 0x6600b4) [C]: an empty clan is one
                // empty entry that ends the list [guess].
                if (members.Count == 0)
                    connection.SendApp(new MediusGetClanMemberListResponse { MessageID = request.MessageID, EndOfList = true });
                for (int i = 0; i < members.Count; i++)
                    connection.SendApp(new MediusGetClanMemberListResponse
                    {
                        MessageID = request.MessageID,
                        AccountID = members[i].AccountId,
                        AccountName = members[i].Name,
                        EndOfList = i == members.Count - 1,
                    });
                return true;
            }

            case MediusGetMyClansRequest request:
            {
                MediusClan? clan = Context.Clans.OfMember(titleId, client.AccountId);
                MediusAccount? leader = clan is null ? null : AccountsOf(client, [clan.LeaderAccountId]).FirstOrDefault();
                connection.SendApp(clan is null
                    ? new MediusGetMyClansResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true }
                    : new MediusGetMyClansResponse
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        ClanID = clan.Id,
                        ApplicationID = connection.AppId,
                        ClanName = clan.Name,
                        LeaderAccountID = clan.LeaderAccountId,
                        LeaderAccountName = leader?.Name ?? "",
                        Stats = new byte[Constants.CLANSTATS_MAXLEN],
                        Status = MediusClanStatus.ClanActive,
                        EndOfList = true,
                    });
                return true;
            }

            case MediusCreateClanRequest request:
            {
                ClanResult result = Context.Clans.Create(titleId, client.AccountId, request.ClanName ?? "", "", LobbyClanRules, out MediusClan? created);
                MediusCallbackStatus status = result switch
                {
                    ClanResult.Done => MediusCallbackStatus.MediusSuccess,
                    ClanResult.NameTaken => MediusCallbackStatus.MediusClanNameInUse,
                    ClanResult.AlreadyInClan => MediusCallbackStatus.MediusAlreadyLeaderOfClan,
                    _ => MediusCallbackStatus.MediusFail,
                };
                Log.Information("{Tag} {Client} created clan '{Name}': {Result} ({Id})", Tag, client, request.ClanName, result, created?.Id);
                connection.SendApp(new MediusCreateClanResponse { MessageID = request.MessageID, StatusCode = status, ClanID = created?.Id ?? -1 });
                return true;
            }

            case MediusCheckMyClanInvitationsRequest request:
            {
                List<MediusClan> invitations = [.. Context.Clans.InvitationsOf(titleId, client.AccountId)];
                if (invitations.Count == 0)
                    connection.SendApp(new MediusCheckMyClanInvitationsResponse
                    {
                        MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, Message = "", LeaderAccountName = "", EndOfList = true,
                    });
                for (int i = 0; i < invitations.Count; i++)
                {
                    MediusAccount? leader = AccountsOf(client, [invitations[i].LeaderAccountId]).FirstOrDefault();
                    connection.SendApp(new MediusCheckMyClanInvitationsResponse
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        // One invitation per clan and player: the clan id names it [guess].
                        ClanInvitationID = invitations[i].Id,
                        ClanID = invitations[i].Id,
                        ResponseStatus = MediusClanInvitationsResponseStatus.ClanInvitationUndecided,
                        Message = "",
                        LeaderAccountID = invitations[i].LeaderAccountId,
                        LeaderAccountName = leader?.Name ?? "",
                        EndOfList = i == invitations.Count - 1,
                    });
                }
                return true;
            }

            case MediusGetClanInvitationsSentRequest request:
            {
                MediusClan? clan = Context.Clans.OfMember(titleId, client.AccountId);
                List<MediusAccount> invited = clan is not null && clan.LeaderAccountId == client.AccountId ? AccountsOf(client, clan.Invited) : [];
                if (invited.Count == 0)
                    connection.SendApp(new MediusGetClanInvitationsSentResponse
                    {
                        MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, AccountName = "", ResponseMsg = "", EndOfList = true,
                    });
                for (int i = 0; i < invited.Count; i++)
                    connection.SendApp(new MediusGetClanInvitationsSentResponse
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        AccountID = invited[i].AccountId,
                        AccountName = invited[i].Name,
                        ResponseMsg = "",
                        ResponseStatus = MediusClanInvitationsResponseStatus.ClanInvitationUndecided,
                        EndOfList = i == invited.Count - 1,
                    });
                return true;
            }
        }
        return false;
    }

    private void SendClanMembersExtraInfo(ScertConnection connection, MediusClient client, MediusGetClanMemberList_ExtraInfoRequest request)
    {
        string titleId = client.Title.Descriptor.TitleId;
        List<MediusAccount> members = Context.Clans.Find(titleId, request.ClanID) is { } clan
            ? AccountsOf(client, clan.Members)
            : [];
        if (members.Count == 0)
            connection.SendApp(new MediusGetClanMemberList_ExtraInfoResponse
            {
                MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, AccountName = "",
                Stats = new byte[Constants.ACCOUNTSTATS_MAXLEN], OnlineState = Offline(), EndOfList = true,
            });
        for (int i = 0; i < members.Count; i++)
        {
            MediusClient? online = Context.Lobby.LoggedIn(client.Title, members[i].Name);
            byte[] stats = new byte[Constants.ACCOUNTSTATS_MAXLEN];
            byte[] stored = Convert.FromBase64String(members[i].Stats);
            stored.AsSpan(0, Math.Min(stats.Length, stored.Length)).CopyTo(stats);
            connection.SendApp(new MediusGetClanMemberList_ExtraInfoResponse
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusSuccess,
                AccountID = members[i].AccountId,
                AccountName = members[i].Name,
                Stats = stats,
                OnlineState = online is null ? Offline() : OnlineState(online),
                LadderPosition = (uint)(i + 1),
                TotalRankings = (uint)members.Count,
                EndOfList = i == members.Count - 1,
            });
        }
    }
}
