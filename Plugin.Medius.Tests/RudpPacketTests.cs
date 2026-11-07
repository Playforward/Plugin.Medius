using Plugin.Medius.Transport;

namespace Plugin.Medius.Tests;

/// <summary>
/// SCE-RT reliable UDP datagrams, taken from a decrypted Resistance 2 session
/// against PSRewired's server (MediusProxy, 2026-10-29). All 19976 complete
/// datagrams of that capture parse and re-serialize byte for byte; these are
/// one of each kind.
/// </summary>
public sealed class RudpPacketTests
{
    [Theory]
    [InlineData("F88B2050752A0000030302000000000002033ACF12A212E7EF40A17DA72F05C5A6ECDC1AEE9F")] // client hello
    [InlineData("F8430100752A")]                                                              // one ack
    [InlineData("F84304007E2A7F2A802A812A")]                                                  // four acks
    [InlineData("F8CB0100165410D0762AF95B199A3F6909B4AEBE819201E8F5FCD0E65666")]              // ack + encrypted reliable
    [InlineData("F8CB010017540150772A01")]                                                    // ack + plain reliable
    [InlineData("F8830D90868F09E1808532F3C360E18E9C2AB1A879")]                                // encrypted unreliable
    [InlineData("F80300000000")]                                                              // no 0x80, an empty message
    [InlineData("F82300405E2C")]                                                              // no 0x80, empty reliable message
    public void Captured_datagrams_round_trip(string hex)
    {
        byte[] data = Convert.FromHexString(hex);
        Assert.True(RudpPacket.TryParse(data, out RudpPacket packet, out string? error), error);
        Assert.Equal(hex, Convert.ToHexString(packet.ToBytes()));
    }

    [Fact]
    public void Hello_is_reliable_with_id_and_nonce()
    {
        Assert.True(RudpPacket.TryParse(Convert.FromHexString("F88B2050752A0000030302000000000002033ACF12A212E7EF40A17DA72F05C5A6ECDC1AEE9F"), out RudpPacket packet, out _));
        Assert.Equal(0xF8, packet.ConnectionId);
        Assert.True(packet.Connecting);
        RudpMessage message = Assert.Single(packet.Messages);
        Assert.True(message.Reliable);
        Assert.False(message.Encrypted);
        Assert.Equal(0x1000, message.UnknownHeaderBits);
        Assert.Equal(0x2A75, message.Sequence);
        Assert.True(RudpHello.TryParse(message.Payload, out RudpHello hello));
        Assert.Equal("3ACF12A2", Convert.ToHexString(hello.Id));
        Assert.Equal("12E7EF40A17DA72F05C5A6ECDC1AEE9F", Convert.ToHexString(hello.Nonce));
        Assert.Equal(message.Payload, hello.ToBytes());
    }

    [Fact]
    public void Encrypted_messages_carry_an_rc_client_session_hash()
    {
        Assert.True(RudpPacket.TryParse(Convert.FromHexString("F8CB0100165410D0762AF95B199A3F6909B4AEBE819201E8F5FCD0E65666"), out RudpPacket packet, out _));
        Assert.Equal([0x5416], packet.Acks);
        RudpMessage message = Assert.Single(packet.Messages);
        Assert.True(message.Encrypted);
        Assert.True(message.Reliable);
        Assert.Equal(16, message.Payload.Length);
        Assert.Equal(3, message.HashContext);
    }

    [Fact]
    public void Truncated_datagrams_are_rejected()
    {
        Assert.False(RudpPacket.TryParse(Convert.FromHexString("F8CB0100165410D0762AF95B"), out _, out string? error));
        Assert.Contains("runs past", error);
    }
}
