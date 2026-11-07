using System.Buffers.Binary;
using System.Text;

namespace Plugin.Medius.Extensions.Resistance2;

/// <summary>
/// One message of Resistance 2's own network layer, the payload of the DME's
/// APP_SINGLE / APP_LIST / APP_BROADCAST: <c>[category u8][type u8][fields]</c>.
/// Category 0 is the peer session (fixed sizes, little-endian numbers); category
/// 2 is the game (most with a big-endian u16 length that counts itself). Every
/// player and the game server's host seat speak it to each other through the
/// relay. Layouts read from R2's session with PSRewired, decrypted
/// (MediusProxy 2026-10-29) [live]; field names are guesses unless said.
/// </summary>
public abstract class R2NetMessage
{
    public const byte Session = 0x00;
    public const byte Game = 0x02;

    public abstract byte Category { get; }
    public abstract byte Type { get; }

    /// <summary>Reads a message a player sent. Unknown types come back as <see cref="R2RawMessage"/>.</summary>
    public static R2NetMessage Parse(byte[] data)
    {
        if (data.Length < 2)
            return new R2RawMessage(data);
        ReadOnlySpan<byte> body = data.AsSpan(2);
        R2NetMessage? message = (data[0], data[1]) switch
        {
            (Session, R2PeerKey.Id) when body.Length == R2PeerKey.BodyLength => R2PeerKey.Read(body),
            (Session, R2PeerState.Id) when body.Length == R2PeerState.BodyLength => R2PeerState.Read(body),
            (Session, R2PeerStep.Id) when body.Length == R2PeerStep.BodyLength => R2PeerStep.Read(body),
            (Session, R2PeerFlags.Id) when body.Length == 4 => new R2PeerFlags { Flags = BinaryPrimitives.ReadUInt32LittleEndian(body) },
            (Session, R2PeerCounter.Id) when body.Length == 4 => new R2PeerCounter { Value = BinaryPrimitives.ReadUInt32LittleEndian(body) },
            (Session, R2PeerPresent.Id) when body.Length == 1 => new R2PeerPresent { Player = body[0] },
            (Session, R2PlayerInfo.Id) when body.Length == R2PlayerInfo.BodyLength => R2PlayerInfo.Read(body),
            (Game, R2ObjectWrite.Id) => R2ObjectWrite.Read(body),
            (Game, R2ObjectCreate.Id) => R2ObjectCreate.Read(body),
            (Game, R2JoinAnnounce.Id) when body.Length == R2JoinAnnounce.BodyLength => R2JoinAnnounce.Read(body),
            (Game, _) when R2GameMessage.HasLength(data) => new R2GameMessage(data[1], body[2..].ToArray()),
            _ => null,
        };
        return message ?? new R2RawMessage(data);
    }

    public byte[] ToBytes()
    {
        List<byte> bytes = [Category, Type];
        WriteBody(bytes);
        return [.. bytes];
    }

    protected abstract void WriteBody(List<byte> bytes);

    protected static void U16Be(List<byte> bytes, ushort value) { bytes.Add((byte)(value >> 8)); bytes.Add((byte)value); }
    protected static void U32Be(List<byte> bytes, uint value) { U16Be(bytes, (ushort)(value >> 16)); U16Be(bytes, (ushort)value); }
    protected static void U32Le(List<byte> bytes, uint value) { bytes.Add((byte)value); bytes.Add((byte)(value >> 8)); bytes.Add((byte)(value >> 16)); bytes.Add((byte)(value >> 24)); }

    public override string ToString() => $"{GetType().Name} {Convert.ToHexString(ToBytes())}";
}

/// <summary>A message no model reads (yet), kept whole.</summary>
public sealed class R2RawMessage(byte[] data) : R2NetMessage
{
    public byte[] Data { get; } = data;
    public override byte Category => Data.Length > 0 ? Data[0] : (byte)0;
    public override byte Type => Data.Length > 1 ? Data[1] : (byte)0;
    protected override void WriteBody(List<byte> bytes) => bytes.AddRange(Data.Length > 2 ? Data[2..] : []);
}

/// <summary>
/// 00 10, 78 bytes: a peer introduces itself with a 64-byte key. Players send
/// their own; PSRewired's host sends player 0 and an all-zero key [live].
/// </summary>
public sealed class R2PeerKey : R2NetMessage
{
    public const byte Id = 0x10;
    public const int BodyLength = 76;
    public override byte Category => Session;
    public override byte Type => Id;

    public byte Unk02;
    public byte Player;
    public byte[] Unk04 = new byte[10];
    public byte[] Key = new byte[64];

    internal static R2PeerKey Read(ReadOnlySpan<byte> b) => new() { Unk02 = b[0], Player = b[1], Unk04 = b[2..12].ToArray(), Key = b[12..76].ToArray() };
    protected override void WriteBody(List<byte> bytes) { bytes.Add(Unk02); bytes.Add(Player); bytes.AddRange(Unk04); bytes.AddRange(Key); }
}

/// <summary>
/// 00 16, 18 bytes: a peer's session state as seen by the sender: state, player,
/// six bytes (the fifth turns 1 on some repeats), a value, and the player's session
/// id (random per game, all zero for the host) [live]. Seen: players go 2, 3, 4;
/// the host says 5 in a lobby, 4 once the game runs, and reports a joined player
/// as 5 with value 2.
/// </summary>
public sealed class R2PeerState : R2NetMessage
{
    public const byte Id = 0x16;
    public const int BodyLength = 16;
    public override byte Category => Session;
    public override byte Type => Id;

    public byte State;
    public byte Player;
    public byte[] Unk04 = new byte[6];
    public uint Value;
    public byte[] SessionId = new byte[4];

    internal static R2PeerState Read(ReadOnlySpan<byte> b) => new()
    {
        State = b[0], Player = b[1], Unk04 = b[2..8].ToArray(), Value = BinaryPrimitives.ReadUInt32LittleEndian(b[8..]), SessionId = b[12..16].ToArray(),
    };

    protected override void WriteBody(List<byte> bytes) { bytes.Add(State); bytes.Add(Player); bytes.AddRange(Unk04); U32Le(bytes, Value); bytes.AddRange(SessionId); }
}

/// <summary>00 15, 8 bytes: a player reports a session step (3) [live].</summary>
public sealed class R2PeerStep : R2NetMessage
{
    public const byte Id = 0x15;
    public const int BodyLength = 6;
    public override byte Category => Session;
    public override byte Type => Id;

    public byte Step;
    public byte Player;
    public uint Unk04;

    internal static R2PeerStep Read(ReadOnlySpan<byte> b) => new() { Step = b[0], Player = b[1], Unk04 = BinaryPrimitives.ReadUInt32LittleEndian(b[2..]) };
    protected override void WriteBody(List<byte> bytes) { bytes.Add(Step); bytes.Add(Player); U32Le(bytes, Unk04); }
}

/// <summary>00 09, 6 bytes: the host sends 0, a player in a running game 0x00400040 [live].</summary>
public sealed class R2PeerFlags : R2NetMessage
{
    public const byte Id = 0x09;
    public override byte Category => Session;
    public override byte Type => Id;
    public uint Flags;
    protected override void WriteBody(List<byte> bytes) => U32Le(bytes, Flags);
}

/// <summary>00 17, 6 bytes: the host sends 1, players their own count (0x0D, 0x0E) [live].</summary>
public sealed class R2PeerCounter : R2NetMessage
{
    public const byte Id = 0x17;
    public override byte Category => Session;
    public override byte Type => Id;
    public uint Value;
    protected override void WriteBody(List<byte> bytes) => U32Le(bytes, Value);
}

/// <summary>00 12, 3 bytes: a player broadcasts its index [live].</summary>
public sealed class R2PeerPresent : R2NetMessage
{
    public const byte Id = 0x12;
    public override byte Category => Session;
    public override byte Type => Id;
    public byte Player;
    protected override void WriteBody(List<byte> bytes) => bytes.Add(Player);
}

/// <summary>
/// 00 0F, 34 bytes: a peer's info: player, a byte, 18 bytes of fields, a 12-byte
/// name ("UNKNOWN" from players, "R2 HOST" from PSRewired's host) [live].
/// </summary>
public sealed class R2PlayerInfo : R2NetMessage
{
    public const byte Id = 0x0F;
    public const int BodyLength = 32;
    public const int NameLength = 12;
    public override byte Category => Session;
    public override byte Type => Id;

    public byte Player;
    public byte Unk03;
    public byte[] Data = new byte[18];
    public string Name = "";

    internal static R2PlayerInfo Read(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> name = b[20..32];
        int end = name.IndexOf((byte)0);
        return new() { Player = b[0], Unk03 = b[1], Data = b[2..20].ToArray(), Name = Encoding.ASCII.GetString(end < 0 ? name : name[..end]) };
    }

    protected override void WriteBody(List<byte> bytes)
    {
        bytes.Add(Player);
        bytes.Add(Unk03);
        bytes.AddRange(Data);
        byte[] name = new byte[NameLength];
        Encoding.ASCII.GetBytes(Name, 0, Math.Min(Name.Length, NameLength - 1), name, 0);
        bytes.AddRange(name);
    }
}

/// <summary>A game message with the usual length field: <c>02 tt [u16be length incl. itself] body</c> [live].</summary>
public sealed class R2GameMessage(byte type, byte[] body) : R2NetMessage
{
    public override byte Category => Game;
    public override byte Type { get; } = type;
    public byte[] Body { get; } = body;

    /// <summary>The body's first two bytes big-endian.</summary>
    public ushort Kind => Body.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(Body) : (ushort)0;

    /// <summary>
    /// 02 AD: the object an event is about, <c>Kind &gt;&gt; 5</c> (0x4640.. = objects
    /// 0x232.., the ones the client creates with 02 AE; 0x1040 = 0x82) [live].
    /// </summary>
    public int EventObject => Kind >> 5;
    /// <summary>02 AD: the event, <c>Kind &amp; 0x1F</c>.</summary>
    public int EventOp => Kind & 0x1F;

    internal static bool HasLength(byte[] data) => data.Length >= 4 && BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2)) == data.Length - 2;

    protected override void WriteBody(List<byte> bytes) { U16Be(bytes, (ushort)(Body.Length + 2)); bytes.AddRange(Body); }
}

/// <summary>
/// 02 AC from a player: write fields of a replicated object. <c>[u16be length incl.
/// itself][u16be object][u32be field mask][for each set bit, lowest first: size u8,
/// data]</c>. Objects 0x00-0x7F are players (0x3FFFFFFF writes a player's whole
/// record, name included), 0x83 the match settings (map name, ...) [live]. The host
/// answers every write with an <see cref="R2ObjectUpdate"/> to all players.
/// </summary>
public sealed class R2ObjectWrite : R2NetMessage
{
    public const byte Id = 0xAC;
    public override byte Category => Game;
    public override byte Type => Id;

    public ushort Object;
    public uint Mask;
    public List<byte[]> Fields = [];

    /// <summary>The fields' data back to back, as the host's update carries them.</summary>
    public byte[] Data => [.. Fields.SelectMany(f => f)];

    internal static R2ObjectWrite? Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < 8 || BinaryPrimitives.ReadUInt16BigEndian(b) != b.Length)
            return null;
        R2ObjectWrite write = new() { Object = BinaryPrimitives.ReadUInt16BigEndian(b[2..]), Mask = BinaryPrimitives.ReadUInt32BigEndian(b[4..]) };
        int at = 8;
        while (at < b.Length)
        {
            int size = b[at];
            if (at + 1 + size > b.Length)
                return null;
            write.Fields.Add(b.Slice(at + 1, size).ToArray());
            at += 1 + size;
        }
        return write.Fields.Count == System.Numerics.BitOperations.PopCount(write.Mask) ? write : null;
    }

    protected override void WriteBody(List<byte> bytes)
    {
        U16Be(bytes, (ushort)(8 + Fields.Sum(f => 1 + f.Length)));
        U16Be(bytes, Object);
        U32Be(bytes, Mask);
        foreach (byte[] field in Fields)
        {
            bytes.Add((byte)field.Length);
            bytes.AddRange(field);
        }
    }
}

/// <summary>
/// 02 AC from the host: objects' fields, sizes stripped. <c>[u16be count][u16be length
/// incl. count and itself][u8 marker][count x (u16be object, u32be mask, data)]</c>.
/// Marker FF: a live update (the echo of a write, one object); a snapshot for a joining
/// player is split over several messages, 00 on all but the last, 01 on the last; an
/// empty snapshot is count 0 with marker 01 [live: solo lobbies, two-PC join, 2026-10-30].
/// </summary>
public sealed class R2ObjectUpdate : R2NetMessage
{
    public const byte Live = 0xFF;
    public override byte Category => Game;
    public override byte Type => R2ObjectWrite.Id;

    public byte Marker = Live;
    public List<R2ObjectFields> Objects = [];

    public static R2ObjectUpdate For(ushort obj, uint mask, byte[] data) => new() { Objects = [new R2ObjectFields(obj, mask, data)] };

    /// <summary>Bytes of one object in the message.</summary>
    public static int SizeOf(R2ObjectFields obj) => 6 + obj.Data.Length;

    protected override void WriteBody(List<byte> bytes)
    {
        U16Be(bytes, (ushort)Objects.Count);
        U16Be(bytes, (ushort)(5 + Objects.Sum(SizeOf)));
        bytes.Add(Marker);
        foreach (R2ObjectFields obj in Objects)
        {
            U16Be(bytes, obj.Object);
            U32Be(bytes, obj.Mask);
            bytes.AddRange(obj.Data);
        }
    }
}

/// <summary>One object's fields as the host's 02 AC carries them: the mask's fields' data, lowest bit first, no sizes.</summary>
public sealed record R2ObjectFields(ushort Object, uint Mask, byte[] Data);

/// <summary>
/// 02 AE: create an object (or send one whole). <c>[u16be length][u16be object (0x4000:
/// asks the host to echo it back without the bit)][u32be mask][u8 flags][per set bit:
/// size u8, data][trailer]</c>. Players create their own objects (0x84, 0x232..); the host
/// creates each player's (flags 0x21, no fields), the table 0x81 (three 128-byte columns
/// per player index; trailer [count 00 01 01]), the match 0x82 (session ids per index,
/// 8-byte flags; trailer [00 count 00 00]) and the settings 0x83 [live].
/// </summary>
public sealed class R2ObjectCreate : R2NetMessage
{
    public const byte Id = 0xAE;
    public const ushort EchoRequest = 0x4000;
    public override byte Category => Game;
    public override byte Type => Id;

    public ushort Object;
    public uint Mask;
    public byte Flags;
    public List<byte[]> Fields = [];
    public byte[] Trailer = [];

    internal static R2ObjectCreate? Read(ReadOnlySpan<byte> b)
    {
        if (b.Length < 9 || BinaryPrimitives.ReadUInt16BigEndian(b) != b.Length)
            return null;
        R2ObjectCreate create = new() { Object = BinaryPrimitives.ReadUInt16BigEndian(b[2..]), Mask = BinaryPrimitives.ReadUInt32BigEndian(b[4..]), Flags = b[8] };
        int at = 9;
        for (int i = 0; i < System.Numerics.BitOperations.PopCount(create.Mask); i++)
        {
            if (at >= b.Length || at + 1 + b[at] > b.Length)
                return null;
            create.Fields.Add(b.Slice(at + 1, b[at]).ToArray());
            at += 1 + b[at];
        }
        create.Trailer = b[at..].ToArray();
        return create;
    }

    protected override void WriteBody(List<byte> bytes)
    {
        U16Be(bytes, (ushort)(9 + Fields.Sum(f => 1 + f.Length) + Trailer.Length));
        U16Be(bytes, Object);
        U32Be(bytes, Mask);
        bytes.Add(Flags);
        foreach (byte[] field in Fields)
        {
            bytes.Add((byte)field.Length);
            bytes.AddRange(field);
        }
        bytes.AddRange(Trailer);
    }
}

/// <summary>02 AF from the host, 4 bytes: an object is gone (the leaving player's) [live].</summary>
public sealed class R2ObjectDelete : R2NetMessage
{
    public const byte Id = 0xAF;
    public override byte Category => Game;
    public override byte Type => Id;
    public ushort Object;
    protected override void WriteBody(List<byte> bytes) => U16Be(bytes, Object);
}

/// <summary>
/// 02 B0, 19 bytes, from a joining player to everyone: zero, two ids that are also
/// in the player's account stats (0x000755CA, 0x00017565), zero, and its index;
/// the host answers <see cref="R2JoinAnnounceReply"/> [live].
/// </summary>
public sealed class R2JoinAnnounce : R2NetMessage
{
    public const byte Id = 0xB0;
    public const int BodyLength = 17;
    public override byte Category => Game;
    public override byte Type => Id;

    public uint Unk02;
    public uint Unk06;
    public uint Unk0A;
    public uint Unk0E;
    public byte Player;

    internal static R2JoinAnnounce Read(ReadOnlySpan<byte> b) => new()
    {
        Unk02 = BinaryPrimitives.ReadUInt32BigEndian(b), Unk06 = BinaryPrimitives.ReadUInt32BigEndian(b[4..]),
        Unk0A = BinaryPrimitives.ReadUInt32BigEndian(b[8..]), Unk0E = BinaryPrimitives.ReadUInt32BigEndian(b[12..]), Player = b[16],
    };

    protected override void WriteBody(List<byte> bytes) { U32Be(bytes, Unk02); U32Be(bytes, Unk06); U32Be(bytes, Unk0A); U32Be(bytes, Unk0E); bytes.Add(Player); }
}

/// <summary>02 B1, 3 bytes: the host's answer to <see cref="R2JoinAnnounce"/>, result 0 [live].</summary>
public sealed class R2JoinAnnounceReply : R2NetMessage
{
    public const byte Id = 0xB1;
    public override byte Category => Game;
    public override byte Type => Id;
    public byte Result;
    protected override void WriteBody(List<byte> bytes) => bytes.Add(Result);
}
