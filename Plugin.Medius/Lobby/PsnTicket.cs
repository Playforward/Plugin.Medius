using System.Buffers.Binary;
using System.Text;

namespace Plugin.Medius.Lobby;

/// <summary>
/// The parts of a PS3 NP (PSN) ticket a server needs: who the player is.
/// The signature cannot be checked any more (Sony's keys are gone with the
/// service), so the identity is taken at its word, which is all a revival
/// server can do and all an emulator like RPCS3 can offer anyway.
/// </summary>
/// <remarks>
/// Layout: <c>version:u32 size:u32</c>, then typed fields
/// <c>type:u16 length:u16 data</c>, big-endian. Types 0x3000 and up are
/// sections holding more fields. In the body, the first 32-byte string
/// (type 4) is the online ID and the first u64 (type 2) the account id.
/// </remarks>
public sealed record PsnTicket(string OnlineId, ulong UserId, uint Version)
{
    public static PsnTicket? TryParse(ReadOnlySpan<byte> ticket)
    {
        if (ticket.Length < 12)
            return null;
        uint version = BinaryPrimitives.ReadUInt32BigEndian(ticket);
        string? onlineId = null;
        ulong userId = 0;
        Walk(ticket[8..], ref onlineId, ref userId, depth: 0);
        return onlineId is null ? null : new PsnTicket(onlineId, userId, version);
    }

    private static void Walk(ReadOnlySpan<byte> data, ref string? onlineId, ref ulong userId, int depth)
    {
        while (data.Length >= 4 && depth < 4)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(data);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
            if (length > data.Length - 4)
                return;
            ReadOnlySpan<byte> value = data.Slice(4, length);

            if (type >= 0x3000)
                Walk(value, ref onlineId, ref userId, depth + 1);
            else if (type == 2 && length == 8 && userId == 0)
                userId = BinaryPrimitives.ReadUInt64BigEndian(value);
            else if (type == 4 && length == 0x20 && onlineId is null)
                onlineId = Encoding.ASCII.GetString(value).TrimEnd('\0');

            data = data[(4 + length)..];
        }
    }
}
