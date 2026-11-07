using System.Buffers.Binary;

namespace Plugin.Medius.Transport;

/// <summary>
/// One datagram of SCE-RT's reliable UDP transport ("RUdp": the DME's
/// NetErrorRUdp* errors, internal codes 54000-54024 mapped to NetError
/// 0x157C-0x158B by Resistance 2 1.60's 0x847208). Resistance 2 plays over
/// it on UDP 11001 when the lobby answers host type 5 / connection type 5.
/// Layout read from a decrypted session against PSRewired's R2 server
/// (MediusProxy, 2026-10-29: 20000 datagrams, all parse) [live]:
/// <code>
/// conn:u8 flags:u8 [flags &amp; 0x40: count:u16le seq:u16le * count] message*
/// message = header:u16le [header &amp; 0x4000: seq:u16le] payload[header &amp; 0xFFF] [header &amp; 0x8000: hash:4]
/// </code>
/// The hash is SCE-RT's keyed RC hash (its top three bits are the cipher
/// context, 3 = RC_CLIENT_SESSION, on every encrypted message seen); the
/// key is not known yet.
/// </summary>
public sealed class RudpPacket
{
    /// <summary>Connection byte, the same both ways for one connection (seen 0xF5, 0xF8, 0x41, 0x8C) [live].</summary>
    public byte ConnectionId { get; set; }

    /// <summary>Raw flags byte; the named properties below read and write its bits.</summary>
    public byte Flags { get; set; }

    /// <summary>
    /// 0x80: set on almost every packet with messages, but not all (F8 03 0000 0000 and
    /// F8 23 0040 5E2C carry messages without it) [live; meaning unknown].
    /// </summary>
    public bool Flag80 { get => (Flags & 0x80) != 0; set => Flags = Set(Flags, 0x80, value); }

    /// <summary>0x40: an ack block follows [live].</summary>
    public bool HasAcks { get => (Flags & 0x40) != 0; set => Flags = Set(Flags, 0x40, value); }

    /// <summary>0x08: set on the handshake and on the messages that follow it until the connection is up [live; meaning guess].</summary>
    public bool Connecting { get => (Flags & 0x08) != 0; set => Flags = Set(Flags, 0x08, value); }

    /// <summary>Bits 0x37: 0x03 (most packets), 0x02, 0x23 seen [live; meaning unknown].</summary>
    public byte UnknownFlags { get => (byte)(Flags & 0x37); set => Flags = (byte)((Flags & ~0x37) | (value & 0x37)); }

    /// <summary>Sequence numbers of the peer's reliable messages this packet acknowledges.</summary>
    public List<ushort> Acks { get; set; } = [];

    public List<RudpMessage> Messages { get; set; } = [];

    public static bool TryParse(ReadOnlySpan<byte> data, out RudpPacket packet, out string? error)
    {
        packet = new RudpPacket();
        error = null;
        if (data.Length < 2)
        {
            error = $"{data.Length}-byte datagram";
            return false;
        }
        packet.ConnectionId = data[0];
        packet.Flags = data[1];
        int at = 2;
        if (packet.HasAcks)
        {
            if (data.Length < at + 2)
            {
                error = "truncated ack count";
                return false;
            }
            int count = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            at += 2;
            if (data.Length < at + 2 * count)
            {
                error = $"truncated ack block ({count} acks)";
                return false;
            }
            for (int i = 0; i < count; i++, at += 2)
                packet.Acks.Add(BinaryPrimitives.ReadUInt16LittleEndian(data[at..]));
        }
        while (at < data.Length)
        {
            if (!RudpMessage.TryRead(data, ref at, out RudpMessage? message, out error))
                return false;
            packet.Messages.Add(message!);
        }
        return true;
    }

    /// <summary>The datagram; <see cref="Flags"/> is written as set, only the ack bit follows <see cref="Acks"/>.</summary>
    public byte[] ToBytes()
    {
        HasAcks = Acks.Count > 0;
        List<byte> bytes = [ConnectionId, Flags];
        if (HasAcks)
        {
            Span<byte> u16 = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)Acks.Count);
            bytes.AddRange(u16);
            foreach (ushort seq in Acks)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(u16, seq);
                bytes.AddRange(u16);
            }
        }
        foreach (RudpMessage message in Messages)
            message.WriteTo(bytes);
        return [.. bytes];
    }

    public override string ToString() =>
        $"conn {ConnectionId:X2} flags {Flags:X2}" + (Acks.Count > 0 ? $" acks [{string.Join(",", Acks.Select(a => a.ToString("X4")))}]" : "") +
        string.Concat(Messages.Select(m => " | " + m));

    private static byte Set(byte flags, byte bit, bool on) => on ? (byte)(flags | bit) : (byte)(flags & ~bit);
}

/// <summary>One message of an <see cref="RudpPacket"/>.</summary>
public sealed class RudpMessage
{
    public const int HashLength = 4;

    /// <summary>The header's top four bits as sent.</summary>
    public ushort HeaderFlags { get; set; }

    /// <summary>0x4000: carries a sequence number and is acknowledged [live].</summary>
    public bool Reliable { get => (HeaderFlags & 0x4000) != 0; set => HeaderFlags = Set(HeaderFlags, 0x4000, value); }

    /// <summary>0x8000: payload encrypted, 4-byte hash after it [live].</summary>
    public bool Encrypted { get => (HeaderFlags & 0x8000) != 0; set => HeaderFlags = Set(HeaderFlags, 0x8000, value); }

    /// <summary>0x2000 and 0x1000 [live; meaning unknown: 0x2000 on most encrypted reliable traffic, 0x1000 on the handshake].</summary>
    public ushort UnknownHeaderBits { get => (ushort)(HeaderFlags & 0x3000); set => HeaderFlags = (ushort)((HeaderFlags & ~0x3000) | (value & 0x3000)); }

    public ushort Sequence { get; set; }
    public byte[] Payload { get; set; } = [];
    public byte[] Hash { get; set; } = [];

    internal static bool TryRead(ReadOnlySpan<byte> data, ref int at, out RudpMessage? message, out string? error)
    {
        message = null;
        error = null;
        if (data.Length < at + 2)
        {
            error = $"truncated message header at {at}";
            return false;
        }
        ushort header = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
        at += 2;
        RudpMessage m = new() { HeaderFlags = (ushort)(header & 0xF000) };
        int length = header & 0x0FFF;
        if (m.Reliable)
        {
            if (data.Length < at + 2)
            {
                error = $"truncated sequence at {at}";
                return false;
            }
            m.Sequence = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            at += 2;
        }
        int needed = length + (m.Encrypted ? HashLength : 0);
        if (data.Length < at + needed)
        {
            error = $"message of {length}B (+{needed - length}B hash) at {at} runs past the {data.Length}B datagram";
            return false;
        }
        m.Payload = data.Slice(at, length).ToArray();
        at += length;
        if (m.Encrypted)
        {
            m.Hash = data.Slice(at, HashLength).ToArray();
            at += HashLength;
        }
        message = m;
        return true;
    }

    internal void WriteTo(List<byte> bytes)
    {
        Span<byte> u16 = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(u16, (ushort)((HeaderFlags & 0xF000) | (Payload.Length & 0x0FFF)));
        bytes.AddRange(u16);
        if (Reliable)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(u16, Sequence);
            bytes.AddRange(u16);
        }
        bytes.AddRange(Payload);
        if (Encrypted)
            bytes.AddRange(Hash.Length == HashLength ? Hash : new byte[HashLength]);
    }

    /// <summary>Hash context, the top three bits of the hash's last byte (3 = RC_CLIENT_SESSION) [live].</summary>
    public int HashContext => Hash.Length == HashLength ? Hash[3] >> 5 : -1;

    public override string ToString() =>
        $"{(Reliable ? $"rel #{Sequence:X4}" : "unrel")}{(Encrypted ? " enc" : "")} bits {UnknownHeaderBits:X4} {Payload.Length}B {Convert.ToHexString(Payload)}" +
        (Encrypted ? $" hash {Convert.ToHexString(Hash)}" : "");

    private static ushort Set(ushort flags, ushort bit, bool on) => on ? (ushort)(flags | bit) : (ushort)(flags & ~bit);
}

/// <summary>
/// The RUdp handshake message (flags 0x8B, message header 0x5020 = reliable,
/// bit 0x1000, 32 bytes), sent by both sides [live]:
/// <code>
/// 00 00 03 03 02 00 00 00 00 00 02 03 | id:4 (the same both ways) | nonce:16 (each side its own)
/// </code>
/// The first 12 bytes are kept as one field until their meaning is known
/// (03 03 looks like versions, NetErrorRUdpVersionMismatch exists) [guess].
/// </summary>
public sealed class RudpHello
{
    public const int Length = 32;

    public byte[] Prefix { get; set; } = Convert.FromHexString("000003030200000000000203");
    public byte[] Id { get; set; } = new byte[4];
    public byte[] Nonce { get; set; } = new byte[16];

    public static bool TryParse(byte[] payload, out RudpHello hello)
    {
        hello = new RudpHello();
        if (payload.Length != Length)
            return false;
        hello.Prefix = payload[..12];
        hello.Id = payload[12..16];
        hello.Nonce = payload[16..32];
        return true;
    }

    public byte[] ToBytes() => [.. Prefix, .. Id, .. Nonce];

    public override string ToString() => $"prefix {Convert.ToHexString(Prefix)} id {Convert.ToHexString(Id)} nonce {Convert.ToHexString(Nonce)}";
}
