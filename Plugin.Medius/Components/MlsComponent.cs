using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Components;

/// <summary>
/// The lobby: channels, who is in them, chat, and the player's stats blob.
/// Games (created and joined here, played on the DME) are in
/// <c>MlsComponent.Games.cs</c>.
/// </summary>
public sealed partial class MlsComponent(MediusContext context) : MediusComponent(context)
{
    public override string Name => MediusComponentNames.Mls;

    /// <summary>
    /// The client arrives with the access token the MAS gave it; its session
    /// is resumed from that rather than begun again.
    /// </summary>
    protected override bool AcceptConnect(ScertConnection connection, RT_MSG_CLIENT_CONNECT_TCP connect)
    {
        if (!base.AcceptConnect(connection, connect))
            return false;

        MediusClient? client = Context.Lobby.ByAccessToken(connect.AccessToken) ?? Context.Lobby.BySessionKey(connect.SessionKey);
        if (client is null || client.Title != connection.Title)
        {
            Log.Warning("{Tag} {Remote}: no session for access token {Token} / session key {Key}; the client must log in at the MAS first",
                Tag, connection.Remote, connect.AccessToken, connect.SessionKey);
            return false;
        }

        client.Connection?.Disconnect();
        client.Connection = connection;
        client.MediusVersion = connection.MediusVersion;
        connection.Client = client;
        MediusChannel channel = Context.Lobby.Channel(client.Title, (int)connect.TargetWorldId) ?? Context.Lobby.DefaultChannel(client.Title);
        JoinChannel(client, channel);
        return true;
    }

    protected override void HandleMedius(ScertConnection connection, BaseMediusMessage message)
    {
        if (connection.Client is not { } client)
        {
            Unhandled(connection, message);
            return;
        }
        MediusTitle title = client.Title;

        switch (message)
        {
            case MediusChannelListRequest request:
                SendChannelList(connection, title, request.MessageID, extra: false);
                break;
            case MediusChannelList_ExtraInfoRequest request:
                SendChannelList(connection, title, request.MessageID, extra: true);
                break;

            case MediusGetTotalChannelsRequest request:
                connection.SendApp(new MediusGetTotalChannelsResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    Total = (uint)Context.Lobby.Channels(title).Count,
                });
                break;

            case MediusChannelInfoRequest request:
                MediusChannel? info = Context.Lobby.Channel(title, request.MediusWorldID);
                connection.SendApp(info is null
                    ? new MediusChannelInfoResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusChannelNotFound }
                    : new MediusChannelInfoResponse
                    {
                        MessageID = request.MessageID,
                        StatusCode = MediusCallbackStatus.MediusSuccess,
                        LobbyName = info.Name,
                        ActivePlayerCount = info.PlayerCount,
                        MaxPlayers = info.MaxPlayers,
                    });
                break;

            case MediusJoinChannelRequest request:
                Join(connection, client, request);
                break;

            case MediusGetWorldSecurityLevelRequest request:
                connection.SendApp(new MediusGetWorldSecurityLevelResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    MediusWorldID = request.MediusWorldID,
                    AppType = request.AppType,
                    SecurityLevel = request.AppType == MediusApplicationType.MediusAppTypeGame && Context.Lobby.Game(title, request.MediusWorldID) is { HasPassword: true }
                        ? MediusWorldSecurityLevelType.WORLD_SECURITY_PLAYER_PASSWORD
                        : MediusWorldSecurityLevelType.WORLD_SECURITY_NONE,
                });
                break;

            case MediusGetLobbyPlayerNames_ExtraInfoRequest request:
                SendPlayerNames(connection, title, request.MessageID, request.MediusWorldID);
                break;

            case MediusGenericChatMessage chat:
                Chat(client, chat);
                break;

            case MediusUpdateUserState state:
                if (state.UserAction == MediusUserAction.JoinedChatWorld && client.Channel is null)
                    JoinChannel(client, Context.Lobby.DefaultChannel(title));
                // A seat the player gave up before reaching the DME; one in use ends when the DME connection does.
                if (state.UserAction == MediusUserAction.LeftGameWorld && client.GameSeat is { Connection: null } seat)
                    Context.Lobby.LeaveGame(seat);
                break;

            case MediusAccountUpdateStatsRequest request:
                if (client.Account is { } account)
                    Context.Lobby.Accounts.Update(account, a => a.Stats = Convert.ToBase64String(request.Stats));
                connection.SendApp(new MediusAccountUpdateStatsResponse
                {
                    MessageID = request.MessageID,
                    StatusCode = client.Account is null ? MediusCallbackStatus.MediusAccountNotFound : MediusCallbackStatus.MediusSuccess,
                });
                break;

            case MediusPlayerInfoRequest request:
                SendPlayerInfo(connection, title, request);
                break;

            case MediusAccountLogoutRequest request:
                Context.Lobby.EndSession(client);
                connection.Client = null;
                connection.SendApp(new MediusAccountLogoutResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                break;

            case MediusSessionEndRequest request:
                Context.Lobby.EndSession(client);
                connection.Client = null;
                connection.SendApp(new MediusSessionEndResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                break;

            default:
                if (!HandleGame(connection, client, message) && !LobbyCommon.TryHandle(Context, connection, message))
                    Unhandled(connection, message);
                break;
        }
    }

    internal override void Disconnected(ScertConnection connection)
    {
        if (connection.Client is not { } client || client.Connection != connection)
            return;
        client.Connection = null;
        client.Channel?.Leave(client);
        client.Channel = null;
        Log.Information("{Tag} {Client} left the lobby", Tag, client);
    }

    private void Join(ScertConnection connection, MediusClient client, MediusJoinChannelRequest request)
    {
        MediusChannel? channel = Context.Lobby.Channel(client.Title, request.MediusWorldID);
        if (channel is null)
        {
            connection.SendApp(new MediusJoinChannelResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusChannelNotFound });
            return;
        }
        if (!JoinChannel(client, channel))
        {
            connection.SendApp(new MediusJoinChannelResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusWorldIsFull });
            return;
        }
        connection.SendApp(new MediusJoinChannelResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusSuccess,
            ConnectInfo = new NetConnectionInfo
            {
                Type = NetConnectionType.NetConnectionTypeClientServerTCP,
                AccessKey = client.AccessToken,
                SessionKey = client.SessionKey,
                WorldID = channel.Id,
                ServerKey = LobbyCommon.PublicKeyOf(Context.ServerKey),
                AddressList = new NetAddressList
                {
                    AddressList =
                    [
                        new NetAddress { Address = Context.PublicAddress.ToString(), Port = (uint)Context.Settings.PortOf(MediusComponentNames.Mls), AddressType = NetAddressType.NetAddressTypeExternal },
                        new NetAddress { Address = Context.PublicAddress.ToString(), Port = (uint)Context.Settings.PortOf(MediusComponentNames.Nat), AddressType = NetAddressType.NetAddressTypeNATService },
                    ],
                },
            },
        });
    }

    private static bool JoinChannel(MediusClient client, MediusChannel channel)
    {
        if (client.Channel == channel)
            return true;
        if (!channel.Join(client))
            return false;
        client.Channel?.Leave(client);
        client.Channel = channel;
        return true;
    }

    private void SendChannelList(ScertConnection connection, MediusTitle title, MessageId messageId, bool extra)
    {
        IReadOnlyList<MediusChannel> channels = Context.Lobby.Channels(title);
        for (int i = 0; i < channels.Count; i++)
        {
            MediusChannel channel = channels[i];
            bool last = i == channels.Count - 1;
            connection.SendApp(extra
                ? new MediusChannelList_ExtraInfoResponse
                {
                    MessageID = messageId,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    MediusWorldID = channel.Id,
                    LobbyName = channel.Name,
                    PlayerCount = (ushort)channel.PlayerCount,
                    MaxPlayers = (ushort)channel.MaxPlayers,
                    GameWorldCount = (ushort)Context.Lobby.Games(title).Count(g => g.Channel == channel && g.IsListed),
                    SecurityLevel = MediusWorldSecurityLevelType.WORLD_SECURITY_NONE,
                    EndOfList = last,
                }
                : new MediusChannelListResponse
                {
                    MessageID = messageId,
                    StatusCode = MediusCallbackStatus.MediusSuccess,
                    MediusWorldID = channel.Id,
                    LobbyName = channel.Name,
                    PlayerCount = channel.PlayerCount,
                    EndOfList = last,
                });
        }
    }

    private void SendPlayerNames(ScertConnection connection, MediusTitle title, MessageId messageId, int worldId)
    {
        MediusClient[] members = Context.Lobby.Channel(title, worldId)?.Members ?? [];
        if (members.Length == 0)
        {
            connection.SendApp(new MediusGetLobbyPlayerNames_ExtraInfoResponse { MessageID = messageId, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true });
            return;
        }
        for (int i = 0; i < members.Length; i++)
        {
            MediusClient member = members[i];
            connection.SendApp(new MediusGetLobbyPlayerNames_ExtraInfoResponse
            {
                MessageID = messageId,
                StatusCode = MediusCallbackStatus.MediusSuccess,
                AccountID = member.AccountId,
                AccountName = member.AccountName,
                OnlineState = OnlineState(member),
                EndOfList = i == members.Length - 1,
            });
        }
    }

    private void SendPlayerInfo(ScertConnection connection, MediusTitle title, MediusPlayerInfoRequest request)
    {
        MediusAccount? account = Context.Lobby.Accounts.Find(request.AccountID);
        if (account is null || account.TitleId != title.Descriptor.TitleId)
        {
            connection.SendApp(new MediusPlayerInfoResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusAccountNotFound });
            return;
        }
        MediusClient? online = Context.Lobby.LoggedIn(title, account.Name);
        byte[] stats = new byte[Constants.ACCOUNTSTATS_MAXLEN];
        Convert.FromBase64String(account.Stats).AsSpan(0, Math.Min(stats.Length, Convert.FromBase64String(account.Stats).Length)).CopyTo(stats);
        connection.SendApp(new MediusPlayerInfoResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusSuccess,
            AccountName = account.Name,
            ApplicationID = online?.AppId ?? connection.AppId,
            PlayerStatus = online is null ? MediusPlayerStatus.MediusPlayerDisconnected : OnlineState(online).ConnectStatus,
            ConnectionClass = MediusConnectionType.Ethernet,
            Stats = stats,
        });
    }

    private void Chat(MediusClient source, MediusGenericChatMessage chat)
    {
        IEnumerable<MediusClient> targets = chat.MessageType switch
        {
            MediusChatMessageType.Whisper => Context.Lobby.Clients(source.Title).Where(c => c.AccountId == chat.TargetID),
            MediusChatMessageType.BroadcastAcrossEntireUniverse => Context.Lobby.Clients(source.Title).Where(c => c.Channel is not null),
            _ => source.Channel?.Members ?? [],
        };

        foreach (MediusClient target in targets)
        {
            if (target.Connection is not { } connection)
                continue;
            if (target.MediusVersion >= 112)
            {
                connection.SendApp(new MediusGenericChatFwdMessage1
                {
                    TimeStamp = Utils.GetUnixTime(),
                    OriginatorAccountID = source.AccountId,
                    OriginatorAccountName = source.AccountName,
                    MessageType = chat.MessageType,
                    Message = chat.Message,
                });
            }
            else
            {
                connection.SendApp(new MediusGenericChatFwdMessage
                {
                    TimeStamp = Utils.GetUnixTime(),
                    OriginatorAccountID = source.AccountId,
                    OriginatorAccountName = source.AccountName,
                    MessageType = chat.MessageType,
                    Message = chat.Message,
                });
            }
        }
    }

    private static MediusPlayerOnlineState OnlineState(MediusClient client) => new()
    {
        ConnectStatus = client.GameSeat is { Joined: true } ? MediusPlayerStatus.MediusPlayerInGameWorld
            : client.Channel is null ? MediusPlayerStatus.MediusPlayerDisconnected
            : MediusPlayerStatus.MediusPlayerInChatWorld,
        MediusLobbyWorldID = client.Channel?.Id ?? 0,
        MediusGameWorldID = client.Game?.Id ?? 0,
        LobbyName = client.Channel?.Name ?? "",
        GameName = client.Game?.Name ?? "",
    };
}
