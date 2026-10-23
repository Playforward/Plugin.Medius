using NetCoreServer;
using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Cryptography;
using Plugin.Medius.RT.Models;
using System.Net;

namespace Plugin.Medius.Transport;

/// <summary>
/// One client's TCP connection to one component: reassembles RT frames from
/// the byte stream, decrypts them with this connection's ciphers, and frames
/// and encrypts what is sent back.
/// </summary>
/// <remarks>
/// An RT frame is <c>id:u8 length:u16le [hash:4] payload</c>. The top bit of
/// the id marks an encrypted frame, which carries the 4-byte hash whose top
/// three bits name the cipher (<see cref="CipherContext"/>).
/// </remarks>
public sealed class ScertConnection
{
    private const int MaxFrameLength = 16 * 1024;

    private readonly TcpSession _session;
    private readonly RsaKeyPair _serverKey;
    private readonly List<byte> _pending = [];
    private readonly object _sendLock = new();

    internal ScertConnection(TcpSession session, string component, RsaKeyPair serverKey)
    {
        _session = session;
        _serverKey = serverKey;
        Component = component;
        Remote = session.Socket?.RemoteEndPoint as IPEndPoint;
        MediusVersion = 108;
    }

    public Guid Id => _session.Id;
    public string Component { get; }
    public IPEndPoint? Remote { get; }
    public int LocalPort => _session.Server.Port;
    public DateTime LastEchoSent { get; set; } = DateTime.UtcNow;

    /// <summary>The SCE-RT library version the client announced in its hello; 108 until it says.</summary>
    public int MediusVersion { get; private set; }
    public bool IsPs3 => MediusVersion >= 112;
    public CipherService Cipher { get; private set; } = null!;

    /// <summary>Set by the RT connect message.</summary>
    public int AppId { get; set; }
    public MediusTitle? Title { get; set; }

    /// <summary>The logged-in (or reserved) player behind this connection, once there is one.</summary>
    public MediusClient? Client { get; set; }

    /// <summary>Per-component scratch state, for a component that needs more than the client.</summary>
    public object? ComponentState { get; set; }

    public bool Encrypt
    {
        get => Cipher.EnableEncryption;
        set => Cipher.EnableEncryption = value;
    }

    public void SetMediusVersion(int version)
    {
        bool encrypt = Cipher?.EnableEncryption ?? true;
        MediusVersion = version;
        Cipher = IsPs3
            ? new CipherService(new PS3CipherFactory())
            : new CipherService(new PS2CipherFactory());
        Cipher.SetCipher(CipherContext.RSA_AUTH, IsPs3 ? _serverKey.ToPS3() : _serverKey.ToPS2());
        Cipher.EnableEncryption = encrypt;
    }

    /// <summary>Appends received bytes and returns every message they complete.</summary>
    public List<BaseScertMessage> Receive(ReadOnlySpan<byte> data)
    {
        Cipher ??= InitialCipher();
        foreach (byte b in data)
            _pending.Add(b);

        List<BaseScertMessage> messages = [];
        while (_pending.Count >= 3)
        {
            byte id = _pending[0];
            int length = _pending[1] | (_pending[2] << 8);
            bool encrypted = id >= 0x80 && length > 0;
            int header = encrypted ? 7 : 3;
            if (length > MaxFrameLength)
                throw new InvalidDataException($"RT frame of {length} bytes (id 0x{id:X2}) exceeds {MaxFrameLength}");
            if (_pending.Count < header + length)
                break;

            byte[] hash = encrypted ? _pending.GetRange(3, 4).ToArray() : null!;
            byte[] payload = _pending.GetRange(header, length).ToArray();
            _pending.RemoveRange(0, header + length);

            BaseScertMessage? message = BaseScertMessage.Instantiate((RT_MSG_TYPE)(id & 0x7F), encrypted ? hash : null, payload, MediusVersion, Cipher);
            if (message is null)
                continue;

            if (message is RT_MSG_CLIENT_MULTI_APP_TOSERVER multi)
                messages.AddRange(multi.Messages.Where(m => m is not null));
            else
                messages.Add(message);

            // The hello announces the library version, which picks the cipher
            // suite every later frame is read with.
            if (message is RT_MSG_CLIENT_HELLO hello && hello.Parameters is { Length: > 1 })
                SetMediusVersion(hello.Parameters[1]);
        }
        return messages;
    }

    public void Send(BaseScertMessage message)
    {
        Cipher ??= InitialCipher();
        lock (_sendLock)
        {
            foreach (byte[] frame in message.Serialize(MediusVersion, Cipher))
                _session.SendAsync(frame);
        }
    }

    /// <summary>Wraps a Medius message in RT_MSG_SERVER_APP and sends it.</summary>
    public void SendApp(BaseMediusMessage message) => Send(new RT_MSG_SERVER_APP { Message = message });

    public void Disconnect() => _session.Disconnect();

    private CipherService InitialCipher()
    {
        SetMediusVersion(MediusVersion);
        return Cipher;
    }

    public override string ToString() => $"{Component}:{Remote}{(Client is null ? "" : $" ({Client.AccountName})")}";
}
