using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Components;

/// <summary>
/// Authentication: begins the player's session, logs them in (by account
/// name and password on PS2, by PSN ticket on PS3) and hands them the
/// address and access token for the lobby (MLS).
/// </summary>
public sealed class MasComponent(MediusContext context) : MediusComponent(context)
{
    public override string Name => MediusComponentNames.Mas;

    protected override void HandleMedius(ScertConnection connection, BaseMediusMessage message)
    {
        if (connection.Title is not { } title)
        {
            Unhandled(connection, message);
            return;
        }

        switch (message)
        {
            case MediusSessionBeginRequest request:
                BeginSession(connection, title, request.MessageID);
                break;
            case MediusSessionBegin1Request request:
                BeginSession(connection, title, request.MessageID);
                break;
            case MediusExtendedSessionBeginRequest request:
                BeginSession(connection, title, request.MessageID);
                break;

            case MediusSessionEndRequest request:
                if (connection.Client is { } ending)
                    Context.Lobby.EndSession(ending);
                connection.Client = null;
                connection.SendApp(new MediusSessionEndResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                break;

            case MediusSetLocalizationParamsRequest request:
                connection.SendApp(new MediusSetLocalizationParamsResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                break;
            case MediusSetLocalizationParamsRequest1 request:
                connection.SendApp(new MediusSetLocalizationParamsResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                break;

            case MediusDnasSignaturePost:
                // DNAS was the PS2's console check; nothing to verify against any more.
                break;

            case MediusAccountRegistrationRequest request:
                Register(connection, title, request);
                break;

            case MediusAccountLoginRequest request:
                LoginWithPassword(connection, title, request);
                break;

            case MediusTicketLoginRequest request:
                LoginWithTicket(connection, title, request);
                break;

            case MediusAnonymousLoginRequest request:
                LoginAnonymously(connection, title, request);
                break;

            case MediusAccountGetIDRequest request:
                MediusAccount? found = Context.Lobby.Accounts.Find(title.Descriptor.TitleId, request.AccountName);
                connection.SendApp(new MediusAccountGetIDResponse
                {
                    MessageID = request.MessageID,
                    AccountID = found?.AccountId ?? -1,
                    StatusCode = found is null ? MediusCallbackStatus.MediusAccountNotFound : MediusCallbackStatus.MediusSuccess,
                });
                break;

            case MediusAccountLogoutRequest request:
                connection.Client?.Logout();
                connection.SendApp(new MediusAccountLogoutResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                break;

            default:
                if (!LobbyCommon.TryHandle(Context, connection, message))
                    Unhandled(connection, message);
                break;
        }
    }

    private void BeginSession(ScertConnection connection, MediusTitle title, MessageId messageId)
    {
        MediusClient client = Context.Lobby.BeginSession(title, connection.AppId, connection.MediusVersion);
        client.Connection = connection;
        connection.Client = client;
        connection.SendApp(new MediusSessionBeginResponse
        {
            MessageID = messageId,
            SessionKey = client.SessionKey,
            StatusCode = MediusCallbackStatus.MediusSuccess,
        });
    }

    private void Register(ScertConnection connection, MediusTitle title, MediusAccountRegistrationRequest request)
    {
        MediusAccount? account = Context.Lobby.Accounts.Create(title.Descriptor.TitleId, request.AccountName, request.Password);
        connection.SendApp(new MediusAccountRegistrationResponse
        {
            MessageID = request.MessageID,
            StatusCode = account is null ? MediusCallbackStatus.MediusAccountAlreadyExists : MediusCallbackStatus.MediusSuccess,
            AccountID = account?.AccountId ?? -1,
        });
        if (account is not null)
            Log.Information("{Tag} {Title}: registered {Name} (#{Id})", Tag, title, account.Name, account.AccountId);
    }

    private void LoginWithPassword(ScertConnection connection, MediusTitle title, MediusAccountLoginRequest request)
    {
        MediusCallbackStatus status = TryLogin(connection, title, request.Username, request.Password, title.CreateAccountOnLogin, out MediusClient? client);
        if (client is null)
        {
            connection.SendApp(new MediusAccountLoginResponse { MessageID = request.MessageID, StatusCode = status });
            return;
        }
        connection.SendApp(new MediusAccountLoginResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusSuccess,
            AccountID = client.AccountId,
            AccountType = MediusAccountType.MediusMasterAccount,
            MediusWorldID = Context.Lobby.DefaultChannel(title).Id,
            ConnectInfo = LobbyConnectInfo(client),
        });
    }

    /// <summary>
    /// PS3 login. The request carries the PSN ticket; nothing can check its
    /// signature now that Sony's key infrastructure is gone, so the online ID
    /// in it is taken at its word and the account is created on first sight.
    /// </summary>
    private void LoginWithTicket(ScertConnection connection, MediusTitle title, MediusTicketLoginRequest request)
    {
        MediusCallbackStatus status = TryLogin(connection, title, request.Username, password: null, create: true, out MediusClient? client);
        if (client is null)
        {
            connection.SendApp(new MediusTicketLoginResponse { MessageID = request.MessageID, StatusCode = status });
            return;
        }
        connection.SendApp(new MediusTicketLoginResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusSuccess,
            AccountID = client.AccountId,
            AccountType = MediusAccountType.MediusMasterAccount,
            MediusWorldID = Context.Lobby.DefaultChannel(title).Id,
            ConnectInfo = LobbyConnectInfo(client),
        });
    }

    private void LoginAnonymously(ScertConnection connection, MediusTitle title, MediusAnonymousLoginRequest request)
    {
        if (connection.Client is not { } client)
        {
            connection.SendApp(new MediusAnonymousLoginResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusIncorrectLoginStep });
            return;
        }
        // An anonymous player still needs a name the lobby can show; it gets
        // a throwaway account under the display name it asked for.
        string name = string.IsNullOrWhiteSpace(request.SessionDisplayName) ? $"Guest{client.SessionKey}" : request.SessionDisplayName;
        MediusAccount account = Context.Lobby.Accounts.Find(title.Descriptor.TitleId, name)
            ?? Context.Lobby.Accounts.Create(title.Descriptor.TitleId, name, null)!;
        client.Login(account);
        connection.SendApp(new MediusAnonymousLoginResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusSuccess,
            AccountID = account.AccountId,
            AccountType = MediusAccountType.MediusMasterAccount,
            MediusWorldID = Context.Lobby.DefaultChannel(title).Id,
            ConnectInfo = LobbyConnectInfo(client),
        });
    }

    private MediusCallbackStatus TryLogin(ScertConnection connection, MediusTitle title, string name, string? password, bool create, out MediusClient? client)
    {
        client = null;
        if (connection.Client is not { } session)
            return MediusCallbackStatus.MediusIncorrectLoginStep;
        if (string.IsNullOrWhiteSpace(name))
            return MediusCallbackStatus.MediusLoginFailed;

        MediusClient? existing = Context.Lobby.LoggedIn(title, name);
        if (existing is not null && existing != session)
        {
            // The old session is almost always this player's own, left by a
            // crash or a dropped connection; a second login replaces it.
            Log.Information("{Tag} {Name} logged in again; ending the previous session", Tag, name);
            Context.Lobby.EndSession(existing);
            existing.Connection?.Disconnect();
        }

        MediusAccount? account = Context.Lobby.Accounts.Find(title.Descriptor.TitleId, name);
        if (account is null)
        {
            if (!create)
                return MediusCallbackStatus.MediusAccountNotFound;
            account = Context.Lobby.Accounts.Create(title.Descriptor.TitleId, name, password);
            if (account is null)
                return MediusCallbackStatus.MediusLoginFailed;
            Log.Information("{Tag} {Title}: created account {Name} (#{Id}) on first login", Tag, title, account.Name, account.AccountId);
        }
        else if (password is not null && !account.CheckPassword(password))
        {
            return MediusCallbackStatus.MediusInvalidPassword;
        }

        Context.Lobby.Accounts.Update(account, a => a.LastLoginUtc = DateTime.UtcNow);
        session.Login(account);
        client = session;
        Log.Information("{Tag} {Title}: {Account} logged in from {Remote}", Tag, title, client, connection.Remote);
        return MediusCallbackStatus.MediusSuccess;
    }

    private NetConnectionInfo LobbyConnectInfo(MediusClient client) => new()
    {
        Type = NetConnectionType.NetConnectionTypeClientServerTCP,
        AccessKey = client.AccessToken,
        SessionKey = client.SessionKey,
        WorldID = Context.Lobby.DefaultChannel(client.Title).Id,
        ServerKey = LobbyCommon.PublicKeyOf(Context.ServerKey),
        AddressList = new NetAddressList
        {
            AddressList =
            [
                new NetAddress { Address = Context.PublicAddress.ToString(), Port = (uint)Context.Settings.PortOf(MediusComponentNames.Mls), AddressType = NetAddressType.NetAddressTypeExternal },
                new NetAddress { Address = Context.PublicAddress.ToString(), Port = (uint)Context.Settings.PortOf(MediusComponentNames.Nat), AddressType = NetAddressType.NetAddressTypeNATService },
            ],
        },
    };

    internal override void Disconnected(ScertConnection connection)
    {
        // The session stays: the client is on its way to the MLS with it.
        if (connection.Client is { } client && client.Connection == connection)
            client.Connection = null;
    }
}
