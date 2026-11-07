using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.RT.Shared.Stream;

namespace Plugin.Medius.Tests;

/// <summary>
/// MediusVersionServer, which Killzone 2 sends right after connecting to the
/// MLS. Sizes are the client's own (1.29 EBOOT: request codec 0x72d518, 21 +
/// 17 bytes; response size 0x72a528 returns 77, codec 0x72d4b8 21 + 56).
/// </summary>
public sealed class VersionServerTests
{
    // Class and type bytes RT_MSG_SERVER_APP puts before the struct.
    private const int AppHeader = 2;

    private static byte[] AppBytes(BaseMediusMessage message)
    {
        using MemoryStream stream = new();
        using (MessageWriter writer = new(stream) { MediusVersion = 113 })
            new RT_MSG_SERVER_APP { Message = message }.Serialize(writer);
        return stream.ToArray();
    }

    [Fact]
    public void Request_is_38_bytes()
    {
        byte[] bytes = AppBytes(new MediusVersionServerRequest { MessageID = new MessageId("11"), SessionKey = "17938" });
        Assert.Equal(AppHeader + 38, bytes.Length);
    }

    [Fact]
    public void Response_is_77_bytes_with_the_version_after_the_message_id()
    {
        byte[] bytes = AppBytes(new MediusVersionServerResponse { MessageID = new MessageId("11"), VersionServer = "Plugin.Medius" });
        Assert.Equal(AppHeader + 77, bytes.Length);
        Assert.Equal("Plugin.Medius"u8.ToArray(), bytes[(AppHeader + 21)..(AppHeader + 21 + 13)]);
    }
}
