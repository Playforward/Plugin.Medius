using Plugin.Medius.Extensions.Mag;
using Plugin.Medius.Extensions.Zipper;
using Plugin.Medius.Lobby;
using System.Buffers.Binary;
using System.Text;

namespace Plugin.Medius.Tests;

public sealed class ZipperBitStreamTests
{
    [Fact]
    public void Plugin_records_match_the_retail_launcher()
    {
        // The launcher's hello arrived as 02 00 00 00 01: length 2 (LE), plugin 0, type 0x0001.
        List<PluginRecord> records = PluginRecords.Read([0x02, 0x00, 0x00, 0x00, 0x01, 0x0A, 0x00, 0x1F, 0, 2, 0, 0, 6, 0xCC, 0, 0, 0, 0]);
        Assert.Equal(2, records.Count);
        Assert.Equal((0, (ushort)0x01), (records[0].PluginId, records[0].MessageType));
        Assert.Equal((31, (ushort)0x02, 10), (records[1].PluginId, records[1].MessageType, records[1].Data.Length));
    }

    [Fact]
    public void Fields_pack_most_significant_bit_first()
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(0x0036);
        writer.WriteBool(true);
        writer.WriteBool(false);
        writer.WriteU32(0xDEADBEEF);
        writer.WriteString("hi");

        byte[] bytes = writer.ToArray();
        // 0x0036, then bits 1 0, then 0xDEADBEEF starting two bits into byte 2.
        Assert.Equal(new byte[] { 0x00, 0x36, 0b1011_0111, 0xAB, 0x6F, 0xBB, 0xC0 }, bytes[..7]);

        ZipperBitReader reader = new(bytes);
        Assert.Equal(0x0036, reader.ReadU16());
        Assert.True(reader.ReadBool());
        Assert.False(reader.ReadBool());
        Assert.Equal(0xDEADBEEF, reader.ReadU32());
        Assert.Equal("hi", reader.ReadString());
    }

    [Fact]
    public void Key_words_are_least_significant_first()
    {
        Org.BouncyCastle.Math.BigInteger n = new("0102030405060708", 16);
        uint[] words = MapsEnvelope.KeyWords(n);
        Assert.Equal(0x05060708u, words[0]);
        Assert.Equal(0x01020304u, words[1]);
        Assert.All(words[2..], w => Assert.Equal(0u, w));
    }
}

public sealed class PsnTicketTests
{
    [Fact]
    public void Reads_online_id_and_user_id_from_the_body_section()
    {
        byte[] ticket = BuildTicket("ZipperFan_99", 0x1122334455667788);
        PsnTicket? parsed = PsnTicket.TryParse(ticket);
        Assert.NotNull(parsed);
        Assert.Equal("ZipperFan_99", parsed.OnlineId);
        Assert.Equal(0x1122334455667788UL, parsed.UserId);
    }

    /// <summary>A ticket shaped like a PS3 2.1 NP ticket: header, then a 0x3000 body of typed fields.</summary>
    internal static byte[] BuildTicket(string onlineId, ulong userId)
    {
        List<byte> body = [];
        void Field(ushort type, byte[] value)
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteUInt16BigEndian(header, type);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)value.Length);
            body.AddRange(header);
            body.AddRange(value);
        }
        Field(8, new byte[0x14]);                                  // serial id
        Field(1, [0, 0, 0, 0x64]);                                 // issuer id
        Field(7, new byte[8]);                                     // issued
        Field(7, new byte[8]);                                     // expires
        byte[] user = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(user, userId);
        Field(2, user);                                            // user id
        byte[] name = new byte[0x20];
        Encoding.ASCII.GetBytes(onlineId).CopyTo(name, 0);
        Field(4, name);                                            // online id
        Field(8, Encoding.ASCII.GetBytes("us\0\0"));               // region

        List<byte> ticket = [0x21, 0x01, 0x00, 0x00];
        byte[] size = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, (uint)(body.Count + 4));
        ticket.AddRange(size);
        byte[] section = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(section, 0x3000);
        BinaryPrimitives.WriteUInt16BigEndian(section.AsSpan(2), (ushort)body.Count);
        ticket.AddRange(section);
        ticket.AddRange(body);
        return [.. ticket];
    }
}

/// <summary>
/// Plays the retail MAG client's side of MAPS: hello, protocol, ticket,
/// universe list. The RT-level plugin header is the part still unconfirmed
/// against a real client; the message bodies follow mage_g's marshal code.
/// </summary>
[Collection(MediusServerCollection.Name)]
public sealed class MapsTests(LobbyFlowFixture fixture)
{
    [Fact]
    public async Task Hello_protocol_ticket_universes()
    {
        await using MediusTestClient client = new(113);
        await client.ConnectAsync(fixture.MapsPort);
        await client.HandshakeAsync(LobbyFlowFixture.MagAppId);

        // Hello -> ProtocolInfo 1740/0
        SendPlugin(client, w => w.WriteU16(MapsTypes.Hello));
        ZipperBitReader protocol = await ExpectPluginAsync(client, MapsTypes.ProtocolInfo);
        Assert.Equal(1740u, protocol.ReadU32());
        Assert.Equal(0u, protocol.ReadU32());

        // ProtocolInfo -> MAPSHello
        SendPlugin(client, w => { w.WriteU16(MapsTypes.ProtocolInfo); w.WriteU32(1740); w.WriteU32(0); });
        ZipperBitReader hello = await ExpectPluginAsync(client, MapsTypes.MapsHello);
        MapsEnvelope envelope = MapsEnvelope.Read(hello);
        Assert.NotEqual(0u, envelope.RsaKey[15]);
        Assert.True(hello.ReadBool());   // success
        Assert.True(hello.ReadBool());   // online
        Assert.Equal(0x7u, hello.ReadU32());
        hello.ReadString();
        ushort chunkSize = hello.ReadU16();
        Assert.True(chunkSize is > 0 and <= 900);

        // The ticket, in two chunks
        byte[] ticket = PsnTicketTests.BuildTicket("MagTester", 42);
        int split = ticket.Length / 2;
        SendTicketChunk(client, ticket[..split], last: false, transId: 5);
        SendTicketChunk(client, ticket[split..], last: true, transId: 5);
        ZipperBitReader auth = await ExpectPluginAsync(client, MapsTypes.NpAuthenticateResponse);
        Assert.Equal(5, MapsEnvelope.Read(auth).TransId);
        Assert.True(auth.ReadBool());

        // Universe list -> the MAS
        SendPlugin(client, w => { w.WriteU16(MapsTypes.UniverseListRequest); new MapsEnvelope { TransId = 6 }.Write(w); });
        ZipperBitReader universe = await ExpectPluginAsync(client, MapsTypes.UniverseListResponse);
        Assert.Equal(6, MapsEnvelope.Read(universe).TransId);
        Assert.Equal(7u, universe.ReadU32());
        Assert.Equal("MAG Test", universe.ReadString());
        Assert.Equal("127.0.0.1", universe.ReadString());
        Assert.Equal("127.0.0.1", universe.ReadString());
        Assert.Equal((uint)fixture.MasPort, universe.ReadU32());
        Assert.Equal("http://127.0.0.1/mag", universe.ReadString());
        Assert.True(universe.ReadBool());
        Assert.True(universe.ReadBool());
    }

    /// <summary>
    /// The launcher's patch check: an empty file list is what sends it
    /// straight on to bin/mage_g.self. Every field the launcher demarshals
    /// has to be there, or it shows kLauncherNoSCERTConnection.
    /// </summary>
    [Fact]
    public async Task Launcher_patch_check_gets_no_patches()
    {
        await using MediusTestClient client = new(113);
        await client.ConnectAsync(fixture.MapsPort);
        await client.HandshakeAsync(LobbyFlowFixture.MagAppId);

        SendPlugin(client, w =>
        {
            w.WriteU16(MapsTypes.PatchRequest);
            w.WriteI32(0);
            w.WriteI32(0);
            w.WriteString("ABmagprod370048");
            w.WriteString("english");
        });
        ZipperBitReader response = await ExpectPluginAsync(client, MapsTypes.PatchResponse);
        Assert.Equal(0, response.ReadU16());          // files
        for (int i = 0; i < 3; i++)
            response.ReadI32();
        Assert.Equal(0x2000u, response.ReadU32());
        response.ReadU8();
        for (int i = 0; i < 6; i++)
            response.ReadI32();
        Assert.True(response.BitsLeft < 8);           // nothing past the last field but padding
    }

    [Fact]
    public void Patch_response_lists_files_name_kind_size()
    {
        byte[] stream = MapsWriters.PatchResponse([new MapsPatchFile { Name = "patch_001.psarc", Kind = 2, Size = 1234 }]);
        ZipperBitReader reader = new(stream);
        Assert.Equal(MapsTypes.PatchResponse, reader.ReadU16());
        Assert.Equal(1, reader.ReadU16());
        Assert.Equal("patch_001.psarc", reader.ReadString());
        Assert.Equal(2, reader.ReadU8());
        Assert.Equal(1234u, reader.ReadU32());
    }

    private static void SendTicketChunk(MediusTestClient client, byte[] chunk, bool last, int transId) =>
        SendPlugin(client, w =>
        {
            w.WriteU16(MapsTypes.NpAuthenticateRequest);
            new MapsEnvelope { TransId = transId }.Write(w);
            w.WriteU16((ushort)chunk.Length);
            for (int i = 0; i < MapsNpAuthenticateChunk.ChunkBufferLength; i++)
                w.WriteU8(i < chunk.Length ? chunk[i] : (byte)0);
            w.WriteU16(0);
            w.WriteBool(last);
        });

    private static void SendPlugin(MediusTestClient client, Action<ZipperBitWriter> write)
    {
        ZipperBitWriter writer = new();
        write(writer);
        client.Send(new RtClientAppToPlugin { Records = [new PluginRecord(0, writer.ToArray())] });
    }

    private static async Task<ZipperBitReader> ExpectPluginAsync(MediusTestClient client, ushort type)
    {
        RtServerPluginToApp message = await client.ExpectAsync<RtServerPluginToApp>();
        PluginRecord record = Assert.Single(message.Records);
        Assert.Equal(31, record.PluginId);
        ZipperBitReader reader = new(record.Data);
        Assert.Equal(type, reader.ReadU16());
        return reader;
    }
}
