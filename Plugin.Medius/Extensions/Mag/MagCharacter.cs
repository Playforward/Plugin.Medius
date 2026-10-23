using Plugin.Medius.Extensions.Zipper;
using System.Text;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// A MAG soldier as the server keeps it. The fields map onto what the
/// client sends in MagNetMessageCreateCharacterRequest (demarshal mage_g
/// 0x8c39dc) and reads back in MagNetCharacterRecord (0x8bda40).
/// </summary>
public sealed class MagCharacter
{
    public string Name { get; set; } = "";
    /// <summary>Signed byte the client reads with 0x17de30; the PMC (Raven, SVER, Valor).</summary>
    public sbyte Faction { get; set; }
    public byte Choice1 { get; set; }
    public byte Choice2 { get; set; }
    public uint Choice3 { get; set; }
    /// <summary>The player's SceNpId (36 bytes), as the client sent it.</summary>
    public byte[] NpId { get; set; } = new byte[36];
    public uint Id { get; set; } = 1;

    /// <summary>The five loadouts; edited through LoadoutUpdateRequest (0x21F).</summary>
    public MagLoadout[] Loadouts { get; set; } = [new(), new(), new(), new(), new()];

    /// <summary>Reads the body after the envelope: name, faction, two bytes, a u32, the NpId.</summary>
    public static MagCharacter ReadCreateRequest(ZipperBitReader reader)
    {
        MagCharacter character = new()
        {
            Name = ReadShortString(reader),
            Faction = (sbyte)reader.ReadU8(),
            Choice1 = reader.ReadU8(),
            Choice2 = reader.ReadU8(),
            Choice3 = reader.ReadU32(),
        };
        character.NpId = reader.ReadBytes(36);
        return character;
    }

    /// <summary>
    /// MagNetCharacterRecord in the order mage_g 0x8bda40 reads it. Stats and
    /// loadouts start empty: 24 + 5 u32 counters, five loadouts of a byte and
    /// 22 slots (a present bit, then a 12-bit item when set; slot count from
    /// the record constructor's five init(0x16) calls at 0x8c0f4c).
    /// </summary>
    public void WriteRecord(ZipperBitWriter writer)
    {
        WriteShortString(writer, Name);   // +0x1b0, max 32
        writer.WriteU32(Id);              // +0x1d0
        writer.WriteU8((byte)Faction);    // +0x04
        writer.WriteU8(Choice1);          // +0x08
        writer.WriteU8(Choice2);          // +0x0c
        writer.WriteU32(Choice3);         // +0x10
        for (int i = 0; i < 24; i++)      // +0x14..+0x70
            writer.WriteU32(0);
        for (int i = 0; i < 5; i++)       // +0x74..+0x84
            writer.WriteU32(0);
        writer.WriteU8(0);                // +0x88
        foreach (MagLoadout loadout in Loadouts)
            loadout.Write(writer);
        writer.WriteU8(0);                // +0x1a4
        writer.WriteU32(0);               // +0x1a8
        writer.WriteU32(0);               // +0x1ac
        writer.WriteU32(0);               // +0x1d4
        for (int i = 0; i < 5; i++)       // +0x1d8..+0x1e8
            writer.WriteU8(0);
        writer.WriteU32(0);               // +0x1ec
        writer.WriteU32(0);               // +0x1f0
        writer.WriteU8(0);                // +0x1f4
        writer.WriteBool(false);          // +0x1f8
        writer.WriteU8(0);                // +0x1fc
        foreach (byte b in NpId)          // +0x200, 36 bytes
            writer.WriteU8(b);
        writer.WriteU8(0);                // +0x224
        writer.WriteU8(0);                // +0x228
    }

    /// <summary>MAG's short strings: a u8 length, then the characters (reader 0x12e2fc).</summary>
    public static string ReadShortString(ZipperBitReader reader) =>
        Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadU8()));

    public static void WriteShortString(ZipperBitWriter writer, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > 31)
            bytes = bytes[..31];
        writer.WriteU8((byte)bytes.Length);
        foreach (byte b in bytes)
            writer.WriteU8(b);
    }
}

/// <summary>
/// One loadout as mage_g reads it (0x4574c4, and the same body in
/// LoadoutUpdateRequest at 0x4578bc): a byte, then 22 slots, each a present
/// bit followed by a 12-bit item id when set. An empty slot is 0xFFFF in
/// the client.
/// </summary>
public sealed class MagLoadout
{
    public const int Slots = 22;
    public const ushort Empty = 0xFFFF;

    public byte Header { get; set; }
    public ushort[] Items { get; set; } = Enumerable.Repeat(Empty, Slots).ToArray();

    public static MagLoadout Read(ZipperBitReader reader)
    {
        MagLoadout loadout = new() { Header = reader.ReadU8() };
        for (int i = 0; i < Slots; i++)
            loadout.Items[i] = reader.ReadBool() ? (ushort)reader.ReadBits(12) : Empty;
        return loadout;
    }

    public void Write(ZipperBitWriter writer)
    {
        writer.WriteU8(Header);
        foreach (ushort item in Items)
        {
            bool present = item != Empty;
            writer.WriteBool(present);
            if (present)
                writer.WriteBits(item, 12);
        }
    }
}
