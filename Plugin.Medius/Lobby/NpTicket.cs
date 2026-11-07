using System.Buffers.Binary;
using System.Text;

namespace Plugin.Medius.Lobby;

/// <summary>
/// The parts of a PSN (or RPCN) NP ticket that say who the player is. A ticket
/// is a u32 version, a u32 length, then typed items (u16 type, u16 length,
/// payload). The body section (type 0x3000) holds, in order: serial (binary),
/// issuer id (u32), issued and expiry dates (time), subject account id (u64),
/// online id (string), region, domain, service id, status... The account id is
/// what the game's sceNpManagerGetTicketParam(4) returns (Resistance 2 keeps it as
/// its "npId"/"acctId", 1.60 0x77d014 [C]). The signature is not checked:
/// nobody here holds Sony's or RPCN's key. Same layout as Plugin.GameSpy's reader.
/// </summary>
public sealed record NpTicket(ulong AccountId, string OnlineId)
{
    private const ushort TypeU64 = 0x0002;
    private const ushort TypeString = 0x0004;
    private const ushort TypeBody = 0x3000;

    /// <summary>The ticket at the start of <paramref name="raw"/>, or null when it does not parse.</summary>
    public static NpTicket? Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 8)
            return null;
        ReadOnlySpan<byte> body = FindBody(raw[8..]);
        ulong? accountId = null;
        int p = 0;
        while (p + 4 <= body.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(body[p..]);
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(body[(p + 2)..]);
            p += 4;
            if (p + length > body.Length)
                return null;
            ReadOnlySpan<byte> payload = body.Slice(p, length);
            p += length;
            if (type == TypeU64 && length == 8 && accountId is null)
                accountId = BinaryPrimitives.ReadUInt64BigEndian(payload);
            else if (type == TypeString && accountId is not null)
                return new NpTicket(accountId.Value, Encoding.ASCII.GetString(payload).TrimEnd('\0'));
        }
        return null;
    }

    private static ReadOnlySpan<byte> FindBody(ReadOnlySpan<byte> items)
    {
        int p = 0;
        while (p + 4 <= items.Length)
        {
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(items[p..]);
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(items[(p + 2)..]);
            if (p + 4 + length > items.Length)
                break;
            if (type == TypeBody)
                return items.Slice(p + 4, length);
            p += 4 + length;
        }
        return [];
    }

    /// <summary>A ticket with these two items, for tests and tools (no signature).</summary>
    public static byte[] Build(ulong accountId, string onlineId)
    {
        List<byte> body = [];
        void Item(ushort type, byte[] payload)
        {
            body.AddRange([(byte)(type >> 8), (byte)type, (byte)(payload.Length >> 8), (byte)payload.Length]);
            body.AddRange(payload);
        }
        Item(0x0008, new byte[20]);                 // serial
        Item(0x0001, new byte[4]);                  // issuer id
        Item(0x0007, new byte[8]);                  // issued
        Item(0x0007, new byte[8]);                  // expires
        byte[] id = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(id, accountId);
        Item(TypeU64, id);
        byte[] name = new byte[32];
        Encoding.ASCII.GetBytes(onlineId).AsSpan(0, Math.Min(onlineId.Length, 31)).CopyTo(name);
        Item(TypeString, name);

        byte[] ticket = new byte[8 + 4 + body.Count];
        BinaryPrimitives.WriteUInt32BigEndian(ticket, 0x21010000);
        BinaryPrimitives.WriteUInt32BigEndian(ticket.AsSpan(4), (uint)(4 + body.Count));
        BinaryPrimitives.WriteUInt16BigEndian(ticket.AsSpan(8), TypeBody);
        BinaryPrimitives.WriteUInt16BigEndian(ticket.AsSpan(10), (ushort)body.Count);
        body.CopyTo(ticket, 12);
        return ticket;
    }
}
