using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Cryptography;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Components;

/// <summary>
/// One SCE-RT server component. Every component speaks the same RT layer
/// (hello, key exchange, connect, echo); this class answers that, and hands
/// the Medius messages carried inside RT_MSG_CLIENT_APP_TOSERVER to the
/// component.
/// </summary>
/// <remarks>
/// The connect sequence, from the client's side:
/// <code>
/// CLIENT_HELLO            -> SERVER_HELLO (server RSA key; PS3 wraps it in a cert)
/// CLIENT_CRYPTKEY_PUBLIC  -> SERVER_CRYPTKEY_PEER (RC session key, RSA-encrypted to the client)
/// CLIENT_CONNECT_TCP      -> SERVER_CONNECT_REQUIRE           (version &gt; 108)
///                         -> CRYPTKEY_GAME, ACCEPT_TCP, COMPLETE (PS2, 108)
/// CLIENT_CONNECT_READY_REQUIRE -> [CRYPTKEY_GAME (PS2)] SERVER_CONNECT_ACCEPT_TCP
/// CLIENT_CONNECT_READY_TCP     -> SERVER_CONNECT_COMPLETE, SERVER_ECHO
/// </code>
/// </remarks>
public abstract class MediusComponent(MediusContext context)
{
    private static uint _nextScertId = 0x10EC;

    protected MediusContext Context { get; } = context;

    /// <summary>The name listeners are assigned by (<see cref="MediusComponentNames"/>).</summary>
    public abstract string Name { get; }

    protected string Tag => $"[medius/{Name}]";

    internal void Handle(ScertConnection connection, BaseScertMessage message)
    {
        if (Context.Settings.LogMessages)
            Log.Debug("{Tag} {Connection} <- {Message}", Tag, connection, message);

        if (connection.Client is { } client)
            client.LastSeenUtc = DateTime.UtcNow;

        if (Context.ExtensionOf(connection.Title)?.TryHandle(this, connection, message) == true)
            return;

        switch (message)
        {
            case RT_MSG_CLIENT_HELLO:
                connection.Send(new RT_MSG_SERVER_HELLO
                {
                    RsaPublicKey = connection.Encrypt ? Context.ServerKey.N : Org.BouncyCastle.Math.BigInteger.Zero,
                });
                break;

            case RT_MSG_CLIENT_CRYPTKEY_PUBLIC cryptKey when cryptKey.Key is not null:
                connection.Cipher.GenerateCipher(CipherContext.RSA_AUTH, cryptKey.Key.Reverse().ToArray());
                connection.Cipher.GenerateCipher(CipherContext.RC_CLIENT_SESSION);
                connection.Send(new RT_MSG_SERVER_CRYPTKEY_PEER { Key = connection.Cipher.GetPublicKey(CipherContext.RC_CLIENT_SESSION) });
                break;

            case RT_MSG_CLIENT_CONNECT_TCP connect:
                if (!AcceptConnect(connection, connect))
                {
                    connection.Disconnect();
                    break;
                }
                if (connection.MediusVersion > 108)
                {
                    connection.Send(new RT_MSG_SERVER_CONNECT_REQUIRE());
                }
                else
                {
                    SendCryptKeyGame(connection);
                    SendAccept(connection);
                    connection.Send(new RT_MSG_SERVER_CONNECT_COMPLETE { ARG1 = 0x0001 });
                }
                break;

            case RT_MSG_CLIENT_CONNECT_READY_REQUIRE:
                if (!connection.IsPs3)
                    SendCryptKeyGame(connection);
                SendAccept(connection);
                break;

            case RT_MSG_CLIENT_CONNECT_READY_TCP ready:
                ConnectReady(connection, ready);
                break;

            case RT_MSG_CLIENT_ECHO echo:
                connection.Send(new RT_MSG_CLIENT_ECHO { Value = echo.Value });
                break;

            case RT_MSG_SERVER_ECHO:
                break;

            case RT_MSG_CLIENT_APP_TOSERVER app when app.Message is not null:
                HandleMedius(connection, app.Message);
                break;

            case RT_MSG_CLIENT_DISCONNECT:
            case RT_MSG_CLIENT_DISCONNECT_WITH_REASON:
                connection.Disconnect();
                break;

            default:
                if (!HandleOther(connection, message))
                    Log.Warning("{Tag} {Connection}: unhandled RT message {Message}", Tag, connection, message);
                break;
        }
    }

    /// <summary>
    /// Matches the connection to its title by app id and decides whether to
    /// take it. A component that needs the client's session back (MLS, DME)
    /// looks it up here by the access token.
    /// </summary>
    protected virtual bool AcceptConnect(ScertConnection connection, RT_MSG_CLIENT_CONNECT_TCP connect) =>
        BindTitle(connection, connect.AppId);

    /// <summary>Matches the connection to the title that lists <paramref name="appId"/>; false (and logged) when none does.</summary>
    protected bool BindTitle(ScertConnection connection, int appId)
    {
        MediusTitle? title = Context.TitleByAppId(appId);
        if (title is null)
        {
            Log.Warning("{Tag} {Remote} connected with app id {AppId}, which no medius title lists; add it to a title's AppIds",
                Tag, connection.Remote, appId);
            return false;
        }
        connection.AppId = appId;
        connection.Title = title;
        connection.Encrypt = title.Encrypt;
        Log.Information("{Tag} {Remote}: {Title} (app {AppId}, Medius {Version}) connected", Tag, connection.Remote, title, appId, connection.MediusVersion);
        return true;
    }

    /// <summary>The client is ready (RT_MSG_CLIENT_CONNECT_READY_TCP); a lobby component completes the connection here.</summary>
    protected virtual void ConnectReady(ScertConnection connection, RT_MSG_CLIENT_CONNECT_READY_TCP ready)
    {
        connection.Send(new RT_MSG_SERVER_CONNECT_COMPLETE { ARG1 = 0x0001 });
        if (connection.MediusVersion > 108)
            SendEcho(connection);
    }

    /// <summary>Handles one Medius message. Unhandled requests get their default failure so the client does not wait forever.</summary>
    protected virtual void HandleMedius(ScertConnection connection, BaseMediusMessage message) => Unhandled(connection, message);

    /// <summary>RT messages beyond the connect sequence; return false to log them as unhandled.</summary>
    protected virtual bool HandleOther(ScertConnection connection, BaseScertMessage message) => false;

    internal virtual void Disconnected(ScertConnection connection) { }

    protected void Unhandled(ScertConnection connection, BaseMediusMessage message)
    {
        if (message is IMediusRequest request && request.GetDefaultFailedResponse(request) is BaseMediusMessage failure)
        {
            Log.Warning("{Tag} {Connection}: unhandled {Message}; answered with its default failure", Tag, connection, message.GetType().Name);
            connection.SendApp(failure);
        }
        else
        {
            Log.Warning("{Tag} {Connection}: unhandled {Message}", Tag, connection, message);
        }
    }

    internal void SendEcho(ScertConnection connection)
    {
        connection.LastEchoSent = DateTime.UtcNow;
        connection.Send(new RT_MSG_SERVER_ECHO());
    }

    protected static void SendCryptKeyGame(ScertConnection connection)
    {
        if (connection.Cipher.HasKey(CipherContext.RC_CLIENT_SESSION))
            connection.Send(new RT_MSG_SERVER_CRYPTKEY_GAME { Key = connection.Cipher.GetPublicKey(CipherContext.RC_CLIENT_SESSION) });
    }

    /// <summary>Accepts the connection; a lobby component is one player on its own, the DME says where the player sits in its world.</summary>
    protected virtual void SendAccept(ScertConnection connection) =>
        connection.Send(new RT_MSG_SERVER_CONNECT_ACCEPT_TCP
        {
            PlayerId = 0,
            ScertId = Interlocked.Increment(ref _nextScertId),
            PlayerCount = 0x0001,
            IP = connection.Remote?.Address,
        });
}
