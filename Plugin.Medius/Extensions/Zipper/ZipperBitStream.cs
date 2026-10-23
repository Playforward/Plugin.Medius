using System.Text;

namespace Plugin.Medius.Extensions.Zipper;

/// <summary>
/// Zipper Interactive's message bit-stream (MAG, SOCOM 4), as the client's
/// marshal code reads and writes it: bits are packed most significant
/// first, integers are written whole in big-endian bit order, a bool is a
/// single bit, and a string is a 16-bit length followed by 8-bit characters.
/// </summary>
/// <remarks>
/// From mage_g (BCUS98110 1.00): the u16 reader at 0xec5bb0 copies 2-3
/// bytes from <c>bitpos/8</c>, shifts left by <c>bitpos%8</c> and advances 16;
/// the bool reader at 0x12e41c reads 1 bit; the string reader at 0xed1d40
/// reads a u16 length then that many 8-bit characters.
/// </remarks>
public sealed class ZipperBitWriter
{
    private readonly List<byte> _bytes = [];
    private int _bitPosition;

    public int BitLength => _bitPosition;

    public void WriteBits(ulong value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            int byteIndex = _bitPosition >> 3;
            if (byteIndex == _bytes.Count)
                _bytes.Add(0);
            if (((value >> i) & 1) != 0)
                _bytes[byteIndex] |= (byte)(0x80 >> (_bitPosition & 7));
            _bitPosition++;
        }
    }

    public void WriteBool(bool value) => WriteBits(value ? 1UL : 0UL, 1);
    public void WriteU8(byte value) => WriteBits(value, 8);
    public void WriteU16(ushort value) => WriteBits(value, 16);
    public void WriteU32(uint value) => WriteBits(value, 32);
    public void WriteI32(int value) => WriteBits((uint)value, 32);

    public void WriteString(string? value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
        WriteU16((ushort)bytes.Length);
        foreach (byte b in bytes)
            WriteU8(b);
    }

    /// <summary>The bytes written so far, the last one zero-padded.</summary>
    public byte[] ToArray() => [.. _bytes];
}

public sealed class ZipperBitReader(byte[] data)
{
    private int _bitPosition;

    public int BitsLeft => data.Length * 8 - _bitPosition;

    public ulong ReadBits(int count)
    {
        if (count > BitsLeft)
            throw new EndOfStreamException($"wanted {count} bits, {BitsLeft} left");
        ulong value = 0;
        for (int i = 0; i < count; i++)
        {
            int bit = (data[_bitPosition >> 3] >> (7 - (_bitPosition & 7))) & 1;
            value = (value << 1) | (uint)bit;
            _bitPosition++;
        }
        return value;
    }

    public bool ReadBool() => ReadBits(1) != 0;
    public byte ReadU8() => (byte)ReadBits(8);
    public ushort ReadU16() => (ushort)ReadBits(16);
    public uint ReadU32() => (uint)ReadBits(32);
    public int ReadI32() => (int)(uint)ReadBits(32);

    public byte[] ReadBytes(int count)
    {
        byte[] bytes = new byte[count];
        for (int i = 0; i < count; i++)
            bytes[i] = ReadU8();
        return bytes;
    }

    public string ReadString()
    {
        int length = ReadU16();
        return Encoding.UTF8.GetString(ReadBytes(length));
    }
}
