using Plugin.Medius.RT.Cryptography;
using System.Buffers.Binary;
using System.Text;

namespace Plugin.Medius.Transport;

/// <summary>
/// The key material of an <see cref="RudpPacket"/> connection. Nothing is
/// exchanged for it beyond the two hello nonces: both sides derive the session
/// key from the nonces and the AccessKey the lobby handed out in the
/// connection info (MediusJoinGameResponse / MediusMatchFoundResponse).
/// Read from Resistance 2 1.60 [C] and checked against two of our sessions and
/// PSRewired's (every encrypted message decrypts and its hash checks) [live]:
/// <list type="bullet">
/// <item>the hello's id is the RCQ hash of the AccessKey with its NUL
/// terminator (17 bytes, 0x87ce80 on conn+0x94, compared at 0x80a7ec), so the
/// first datagram already names the AccessKey;</item>
/// <item>the session key is derived by 0x870f68 (crypto method +0x60, called
/// at 0x80a8ec) and set as the peer key with type 0x400 (PS3 RCQ) at 0x80a924;</item>
/// <item>each side then sends its own hello nonce encrypted with it (16-byte
/// reliable message, header 0xD010, hash context 3) as the key confirmation
/// (0x80ad60 on the client).</item>
/// </list>
/// </summary>
public static class RudpCrypto
{
    /// <summary>Cipher context of every encrypted RUdp message seen [live].</summary>
    public const CipherContext Context = CipherContext.RC_CLIENT_SESSION;

    /// <summary>The 4-byte id both hellos carry for a connection made with <paramref name="accessKey"/>.</summary>
    public static byte[] HelloId(string accessKey)
    {
        byte[] data = new byte[Align4(accessKey.Length + 1)];
        Encoding.ASCII.GetBytes(accessKey, data);
        uint[] state = Absorb(InitialState(), data);
        byte[] id = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(id, state[0] + state[1] + state[2] + state[3]);
        return id;
    }

    /// <summary>
    /// The 64-byte session key (0x870f68): key[16..32] = server nonce, key[32..48] =
    /// client nonce, key[48..64] = Hash16(AccessKey's 16 characters); then key[0..16]
    /// = Hash16(key[16..64]) and key[16..32], key[32..48], key[48..64] each =
    /// Hash16(key[0..64]) in turn. PS3 RCQ uses the first 16 bytes.
    /// </summary>
    public static byte[] SessionKey(ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> clientNonce, string accessKey)
    {
        byte[] key = new byte[64];
        serverNonce[..16].CopyTo(key.AsSpan(0x10));
        clientNonce[..16].CopyTo(key.AsSpan(0x20));
        Hash16(Encoding.ASCII.GetBytes(accessKey.PadRight(16, '\0')[..16])).CopyTo(key, 0x30);
        Hash16(key.AsSpan(0x10, 0x30)).CopyTo(key, 0x00);
        Hash16(key).CopyTo(key, 0x10);
        Hash16(key).CopyTo(key, 0x20);
        Hash16(key).CopyTo(key, 0x30);
        return key;
    }

    public static PS3_RCQ Cipher(byte[] sessionKey) => new(sessionKey, Context);

    /// <summary>
    /// The crypto library's 16-byte hash (0x86eb5c): the RCQ hash state after the data
    /// (init 0x87ccb0, update 0x87d538), output (0x87cd80) as word i = s[i] ^ s0^s1^s2^s3,
    /// little-endian.
    /// </summary>
    public static byte[] Hash16(ReadOnlySpan<byte> data)
    {
        byte[] padded = new byte[Align4(data.Length)];
        data.CopyTo(padded);
        uint[] s = Absorb(InitialState(), padded);
        uint x = s[0] ^ s[1] ^ s[2] ^ s[3];
        byte[] output = new byte[16];
        for (int i = 0; i < 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(i * 4), s[i] ^ x);
        return output;
    }

    private static int Align4(int length) => (length + 3) & ~3;

    /// <summary>The state after one pass over 16 zero bytes, as PS3_RCQ's hash IV.</summary>
    private static uint[] InitialState() => Absorb(new uint[4], new byte[16]);

    /// <summary>The RCQ rounds over little-endian words (0x87d5bc).</summary>
    private static uint[] Absorb(uint[] state, ReadOnlySpan<byte> data)
    {
        uint r16 = state[0], r17 = state[1], r18 = state[2], r19 = state[3];
        for (int i = 0; i + 4 <= data.Length; i += 4)
        {
            r19 ^= 0x5B3AA654;
            r18 = uint.RotateLeft(r18 + r16 + r19, 7);
            r17 = uint.RotateLeft(r17 + r19 + r18, 11);
            r18 ^= 0x75970A4D;
            r16 = uint.RotateLeft(r16 + r18 + r17, 17);
            r19 += (r16 & r17) | (r18 & ~r17);
            r17 = ~r17;
            r16 = ~r16;
            r19 ^= BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
        }
        return [r16, r17, r18, r19];
    }
}
