using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Cryptography;
using Plugin.Medius.RT.Models;

namespace Plugin.Medius.Tests;

/// <summary>
/// RT_MSG_CLIENT_APP_LIST / APP_SINGLE whose target is sent in the clear and only the
/// payload encrypted (Killzone 2 1.29's DME APP_LIST failed the whole-message
/// decrypt, starting 04 00 00 00 00 / 05 00 00 00 00 00 [live 2026-10-31]).
/// </summary>
public sealed class AppListCipherTests
{
    [Fact]
    public void Clear_target_list_with_an_encrypted_payload_reads_back()
    {
        CipherService cipher = new(new PS3CipherFactory()) { EnableEncryption = true };
        cipher.GenerateCipher(CipherContext.RC_CLIENT_SESSION);
        byte[] payload = [0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70];
        Assert.True(cipher.Encrypt(CipherContext.RC_CLIENT_SESSION, payload, out byte[] encrypted, out byte[] hash));

        byte[] message = [1, 0b0000_0001, .. encrypted];
        RT_MSG_CLIENT_APP_LIST list = Assert.IsType<RT_MSG_CLIENT_APP_LIST>(
            BaseScertMessage.Instantiate(RT_MSG_TYPE.RT_MSG_CLIENT_APP_LIST, hash, message, 113, cipher));
        Assert.Equal([0], list.Targets);
        Assert.Equal(payload, list.Payload);
    }

    [Fact]
    public void Clear_target_with_an_encrypted_payload_reads_back()
    {
        CipherService cipher = new(new PS3CipherFactory()) { EnableEncryption = true };
        cipher.GenerateCipher(CipherContext.RC_CLIENT_SESSION);
        byte[] payload = [0xB6, 0x96, 0xEC, 0x60, 0x81];
        Assert.True(cipher.Encrypt(CipherContext.RC_CLIENT_SESSION, payload, out byte[] encrypted, out byte[] hash));

        byte[] message = [0x01, 0x00, .. encrypted];
        RT_MSG_CLIENT_APP_SINGLE single = Assert.IsType<RT_MSG_CLIENT_APP_SINGLE>(
            BaseScertMessage.Instantiate(RT_MSG_TYPE.RT_MSG_CLIENT_APP_SINGLE, hash, message, 113, cipher));
        Assert.Equal((short)1, single.TargetOrSource);
        Assert.Equal(payload, single.Payload);
    }
}
