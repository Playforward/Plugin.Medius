using NetCoreServer;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Cryptography;
using Plugin.Medius.RT.Models;
using System.Net;

namespace Plugin.Medius.Transport;

/// <summary>
/// A player's aux UDP endpoint on the DME's shared UDP listener. Datagrams
/// carry the same RT frames as TCP, several to a datagram, each whole.
/// </summary>
/// <remarks>
/// What the server sends here goes unencrypted, as Horizon's DME does with
/// the retail games it serves. What arrives is decrypted with the player's
/// TCP session cipher when a frame is marked encrypted.
/// </remarks>
public sealed class ScertDatagramPeer(UdpServer server, EndPoint remote, int mediusVersion)
{
    public EndPoint Remote { get; } = remote;
    public int MediusVersion { get; } = mediusVersion;

    public void Send(BaseScertMessage message)
    {
        foreach (byte[] frame in message.Serialize(MediusVersion, null!))
            server.SendAsync(Remote, frame);
    }

    /// <summary>
    /// Splits one datagram into its RT messages. An encrypted frame needs
    /// <paramref name="cipher"/>; without one it is skipped. A truncated
    /// frame ends the datagram.
    /// </summary>
    public static List<BaseScertMessage> Parse(ReadOnlySpan<byte> datagram, int mediusVersion, CipherService? cipher)
    {
        List<BaseScertMessage> messages = [];
        while (datagram.Length >= 3)
        {
            byte id = datagram[0];
            int length = datagram[1] | (datagram[2] << 8);
            bool encrypted = id >= 0x80 && length > 0;
            int header = encrypted ? 7 : 3;
            if (datagram.Length < header + length)
                break;

            byte[] hash = encrypted ? datagram.Slice(3, 4).ToArray() : null!;
            byte[] payload = datagram.Slice(header, length).ToArray();
            datagram = datagram[(header + length)..];
            if (encrypted && cipher is null)
                continue;

            BaseScertMessage? message = BaseScertMessage.Instantiate((RT_MSG_TYPE)(id & 0x7F), hash, payload, mediusVersion, cipher!);
            if (message is RT_MSG_CLIENT_MULTI_APP_TOSERVER multi)
                messages.AddRange(multi.Messages.Where(m => m is not null));
            else if (message is not null)
                messages.Add(message);
        }
        return messages;
    }

    public override string ToString() => Remote.ToString() ?? "";
}
