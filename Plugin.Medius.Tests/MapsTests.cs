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
        SendPlugin(client, w => w.WriteU16(MagIds.Retail.Hello));
        ZipperBitReader protocol = await ExpectPluginAsync(client, MagIds.Retail.ProtocolInfo);
        Assert.Equal(1740u, protocol.ReadU32());
        Assert.Equal(0u, protocol.ReadU32());

        // ProtocolInfo -> MAPSHello
        SendPlugin(client, w => { w.WriteU16(MagIds.Retail.ProtocolInfo); w.WriteU32(1740); w.WriteU32(0); });
        ZipperBitReader hello = await ExpectPluginAsync(client, MagIds.Retail.MapsHello);
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
        ZipperBitReader auth = await ExpectPluginAsync(client, MagIds.Retail.NpAuthenticateResponse);
        Assert.Equal(5, MapsEnvelope.Read(auth).TransId);
        Assert.True(auth.ReadBool());

        // Universe list -> the MAS
        SendPlugin(client, w => { w.WriteU16(MagIds.Retail.UniverseListRequest); new MapsEnvelope { TransId = 6 }.Write(w); });
        ZipperBitReader universe = await ExpectPluginAsync(client, MagIds.Retail.UniverseListResponse);
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
    /// A soldier created once comes back in the character list on the next
    /// login, so MAG goes to character select instead of creation.
    /// </summary>
    [Fact]
    public async Task Created_character_is_listed_on_the_next_login()
    {
        await using (MediusTestClient first = new(113))
        {
            await LoginAsync(first, "MagSoldier", 43);
            SendPlugin(first, w =>
            {
                w.WriteU16(MagIds.Retail.CreateCharacterRequest);
                new MapsEnvelope { TransId = 7 }.Write(w);
                MagCharacter.WriteShortString(w, "Sarge");
                w.WriteU8(2);                 // faction
                w.WriteU8(0);
                w.WriteU8(0);
                w.WriteU32(1);
                for (int i = 0; i < 36; i++)
                    w.WriteU8(0);
            });
            ZipperBitReader created = await ExpectPluginAsync(first, MagIds.Retail.CreateCharacterResponse);
            Assert.Equal(7, MapsEnvelope.Read(created).TransId);
            Assert.Equal(0, created.ReadI32());
        }

        await using MediusTestClient second = new(113);
        await LoginAsync(second, "MagSoldier", 43);
        SendPlugin(second, w => { w.WriteU16(MagIds.Retail.CharacterListRequest); new MapsEnvelope { TransId = 8 }.Write(w); });
        ZipperBitReader list = await ExpectPluginAsync(second, MagIds.Retail.CharacterListResponse);
        Assert.Equal(8, MapsEnvelope.Read(list).TransId);
        Assert.True(list.ReadBool());
        Assert.Equal(1, list.ReadU8());
        Assert.Equal(1, list.ReadU8());               // one summary
        Assert.Equal("Sarge", MagCharacter.ReadShortString(list));
        list.ReadU32();                               // id
        Assert.Equal(2, list.ReadU8());               // faction
        Assert.Equal(4, list.ReadU8());
        list.ReadU8();
        MagLoadout loadout = MagLoadout.Read(list);
        Assert.All(loadout.Items, item => Assert.Equal(MagLoadout.Empty, item));
        list.ReadU32();
        list.ReadU8();
        list.ReadBool();
        list.ReadU8();
        list.ReadU8();
        list.ReadU8();
        Assert.True(list.BitsLeft < 8);
    }

    /// <summary>
    /// Deploying needs a game server plugin (Plugin.MagGameServer, tested in
    /// its own repo). Without one the lobby still answers NetJoinGameRequest,
    /// with a failure, so the client does not sit waiting for it.
    /// </summary>
    [Fact]
    public async Task Join_game_fails_without_a_game_server()
    {
        await using MediusTestClient lobby = new(113);
        await LoginAsync(lobby, "MagDeployer", 44);
        SendPlugin(lobby, w =>
        {
            w.WriteU16(MagIds.Retail.JoinGameRequest);
            new MapsEnvelope { TransId = 22 }.Write(w);
            new MagJoinGameRequest(1, MagClientVersion.Retail.GameTypes.IdOf(MagGameTypes.Suppression)!.Value, 0, 0, 0).Write(w);
        });
        ZipperBitReader join = await ExpectPluginAsync(lobby, MagIds.Retail.JoinGameResponse);
        Assert.Equal(22, MapsEnvelope.Read(join).TransId);
        Assert.Equal(1, join.ReadU8());                         // failure
    }

    [Fact]
    public async Task Purchased_skill_spends_a_point_and_is_in_the_record()
    {
        await using MediusTestClient client = new(113);
        await LoginWithCharacterAsync(client, "MagBuyer", 44);

        // A new soldier has level 1's point and the 10 StartingSkillPoints; skill 37 costs 2.
        SendPlugin(client, w => { w.WriteU16(MagIds.Retail.PurchaseSkillRequest); w.WriteU8(37); });
        ZipperBitReader bought = await ExpectPluginAsync(client, MagIds.Retail.PurchaseSkillResponse);
        Assert.True(bought.ReadBool());
        WireRecord record = WireRecord.Read(bought);
        Assert.Equal(1u, record.Skills[0]);               // skill 0, the free recruit kit
        Assert.Equal(1u << 5, record.Skills[1]);          // skill 37 = word 1, bit 5
        Assert.Equal(9, record.SkillPoints);
        Assert.NotEqual(0u, record.AvailableItems[3] & (1u << (111 & 31)));   // its SVER foregrip, item bit 111
        Assert.True(bought.BitsLeft < 8);

        // Buying it again fails, and the reply still carries the (unchanged) record.
        SendPlugin(client, w => { w.WriteU16(MagIds.Retail.PurchaseSkillRequest); w.WriteU8(37); });
        ZipperBitReader again = await ExpectPluginAsync(client, MagIds.Retail.PurchaseSkillResponse);
        Assert.False(again.ReadBool());
        Assert.Equal(9, WireRecord.Read(again).SkillPoints);
    }

    [Fact]
    public async Task Skill_respec_refunds_skills_for_respec_points()
    {
        await using MediusTestClient client = new(113);
        await LoginWithCharacterAsync(client, "MagRespec", 45);
        // Tier 1 Assault first: skill 1 (tier 2) needs 2 points spent there.
        foreach (byte skill in new byte[] { 37, 1 })
        {
            SendPlugin(client, w => { w.WriteU16(MagIds.Retail.PurchaseSkillRequest); w.WriteU8(skill); });
            Assert.True((await ExpectPluginAsync(client, MagIds.Retail.PurchaseSkillResponse)).ReadBool());
        }

        SendPlugin(client, w => w.WriteU16(MagIds.Retail.SkillReSpecRequest));
        ZipperBitReader respec = await ExpectPluginAsync(client, MagIds.Retail.SkillReSpecResponse);
        Assert.True(respec.ReadBool());
        WireRecord record = WireRecord.Read(respec);
        Assert.Equal(new uint[] { 1, 0, 0, 0 }, record.Skills);   // only the free skill, bought again
        Assert.Equal(11, record.SkillPoints);
        Assert.Equal(1, record.RespecCount);
        Assert.Equal(0u, record.RespecPoints);

        // The 3000 starting respec points are spent and the next respec needs 5000: no record follows a failure.
        SendPlugin(client, w => w.WriteU16(MagIds.Retail.SkillReSpecRequest));
        ZipperBitReader refused = await ExpectPluginAsync(client, MagIds.Retail.SkillReSpecResponse);
        Assert.False(refused.ReadBool());
        Assert.True(refused.BitsLeft < 8);
    }

    [Fact]
    public async Task Faction_respec_moves_pmc_and_answers_with_the_summary()
    {
        await using MediusTestClient client = new(113);
        await LoginWithCharacterAsync(client, "MagTurncoat", 46);   // created in faction 2

        SendReSpec(client, MagIds.Retail.FactionReSpecRequest, 2);
        ZipperBitReader same = await ExpectPluginAsync(client, MagIds.Retail.CharacterReSpecResponse);
        Assert.Equal(1, same.ReadU8());
        Assert.True(same.BitsLeft < 8);

        SendReSpec(client, MagIds.Retail.FactionReSpecRequest, 0);
        ZipperBitReader moved = await ExpectPluginAsync(client, MagIds.Retail.CharacterReSpecResponse);
        Assert.Equal(0, moved.ReadU8());
        Assert.Equal("Soldier46", MagCharacter.ReadShortString(moved));
        moved.ReadU32();                                  // id
        Assert.Equal(0, moved.ReadU8());                  // faction
        moved.ReadU8();                                   // skill specialty
        Assert.Equal(1, moved.ReadU8());                  // level
        MagLoadout.Read(moved);
        Assert.Equal(7u, moved.ReadU32());                // universe
        Assert.Equal(4 | 1, moved.ReadU8());              // faction history: 2 then 0
    }

    [Fact]
    public async Task Veteran_respec_needs_max_level()
    {
        await using MediusTestClient client = new(113);
        await LoginWithCharacterAsync(client, "MagRookie", 47);

        SendReSpec(client, MagIds.Retail.VeteranReSpecRequest, 1);
        ZipperBitReader refused = await ExpectPluginAsync(client, MagIds.Retail.CharacterReSpecResponse);
        Assert.Equal(1, refused.ReadU8());
        Assert.True(refused.BitsLeft < 8);
    }

    /// <summary>
    /// Deleting a loadout moves the later ones up and clears the last;
    /// SelectLoadout (no reply) picks the one character select shows.
    /// </summary>
    [Fact]
    public async Task Loadouts_delete_and_select()
    {
        await using MediusTestClient client = new(113);
        await LoginWithCharacterAsync(client, "MagQuartermaster", 48);
        for (byte i = 0; i < 3; i++)
        {
            MagLoadout loadout = new() { Header = i };
            loadout.Items[0] = (ushort)(100 + i);
            SendPlugin(client, w => { w.WriteU16(MagIds.Retail.LoadoutUpdateRequest); w.WriteU8(i); loadout.Write(w); });
            Assert.True((await ExpectPluginAsync(client, MagIds.Retail.LoadoutUpdateResponse)).ReadBool());
        }

        SendPlugin(client, w => { w.WriteU16(MagIds.Retail.LoadoutDeleteRequest); w.WriteU8(0); });
        Assert.True((await ExpectPluginAsync(client, MagIds.Retail.LoadoutDeleteResponse)).ReadBool());
        SendPlugin(client, w => { w.WriteU16(MagIds.Retail.LoadoutDeleteRequest); w.WriteU8(5); });
        Assert.False((await ExpectPluginAsync(client, MagIds.Retail.LoadoutDeleteResponse)).ReadBool());

        SendPlugin(client, w => { w.WriteU16(MagIds.Retail.SelectLoadout); w.WriteU8(1); });
        SendPlugin(client, w => { w.WriteU16(MagIds.Retail.CharacterDataRequest); new MapsEnvelope { TransId = 9 }.Write(w); w.WriteU8(0); MagCharacter.WriteShortString(w, "Soldier48"); });
        ZipperBitReader data = await ExpectPluginAsync(client, MagIds.Retail.CharacterDataResponse);
        Assert.Equal(9, MapsEnvelope.Read(data).TransId);
        Assert.True(data.ReadBool());
        data.ReadU8();
        WireRecord record = WireRecord.Read(data);
        Assert.Equal(new ushort[] { 101, 102, MagLoadout.Empty, MagLoadout.Empty, MagLoadout.Empty }, record.Loadouts.Select(l => l.Items[0]));
        Assert.Equal(1, record.LastUsedLoadout);
    }

    /// <summary>
    /// Deleting the soldier (to make one in another PMC): the request names it
    /// and its id, a mismatch is refused, and once deleted the character list
    /// is empty, so the client goes to character creation.
    /// </summary>
    [Fact]
    public async Task Deleted_character_leaves_the_list_empty()
    {
        await using MediusTestClient client = new(113);
        await LoginWithCharacterAsync(client, "MagDeserter", 49);

        SendDelete(client, 10, "SomeoneElse", 1);
        ZipperBitReader refused = await ExpectPluginAsync(client, MagIds.Retail.DeleteCharacterResponse);
        Assert.Equal(10, MapsEnvelope.Read(refused).TransId);
        Assert.False(refused.ReadBool());

        SendDelete(client, 11, "Soldier49", 1);
        ZipperBitReader deleted = await ExpectPluginAsync(client, MagIds.Retail.DeleteCharacterResponse);
        Assert.Equal(11, MapsEnvelope.Read(deleted).TransId);
        Assert.True(deleted.ReadBool());

        SendPlugin(client, w => { w.WriteU16(MagIds.Retail.CharacterListRequest); new MapsEnvelope { TransId = 12 }.Write(w); });
        ZipperBitReader list = await ExpectPluginAsync(client, MagIds.Retail.CharacterListResponse);
        MapsEnvelope.Read(list);
        Assert.True(list.ReadBool());
        list.ReadU8();
        Assert.Equal(0, list.ReadU8());               // no summaries
    }

    /// <summary>DeleteCharacterRequest as the lobby builds it (0x8206c): name, then character id.</summary>
    private static void SendDelete(MediusTestClient client, int transId, string name, uint id) =>
        SendPlugin(client, w =>
        {
            w.WriteU16(MagIds.Retail.DeleteCharacterRequest);
            new MapsEnvelope { TransId = transId }.Write(w);
            w.WriteString(name);
            w.WriteU32(id);
        });

    /// <summary>Logs in and creates "Soldier{userId}" in faction 2, universe 7, with the default starting points.</summary>
    private async Task LoginWithCharacterAsync(MediusTestClient client, string onlineId, ulong userId)
    {
        await LoginAsync(client, onlineId, userId);
        SendPlugin(client, w =>
        {
            w.WriteU16(MagIds.Retail.CreateCharacterRequest);
            new MapsEnvelope { TransId = 2 }.Write(w);
            MagCharacter.WriteShortString(w, $"Soldier{userId}");
            w.WriteU8(2);
            w.WriteU8(3);
            w.WriteU8(4);
            w.WriteU32(7);
            for (int i = 0; i < 36; i++)
                w.WriteU8(0);
        });
        ZipperBitReader created = await ExpectPluginAsync(client, MagIds.Retail.CreateCharacterResponse);
        MapsEnvelope.Read(created);
        Assert.Equal(0, created.ReadI32());
    }

    private static void SendReSpec(MediusTestClient client, ushort type, sbyte faction) =>
        SendPlugin(client, w => { w.WriteU16(type); w.WriteU32(0); w.WriteU8((byte)faction); });

    /// <summary>MagNetCharacterRecord as the client reads it (mage_g 0x8bda40), keeping the fields the tests look at.</summary>
    private sealed record WireRecord(uint[] AvailableItems, uint[] Skills, byte SkillPoints, MagLoadout[] Loadouts, byte RespecCount, uint RespecPoints, byte LastUsedLoadout)
    {
        public static WireRecord Read(ZipperBitReader r)
        {
            MagCharacter.ReadShortString(r);
            r.ReadU32();
            r.ReadU8(); r.ReadU8(); r.ReadU8(); r.ReadU32();
            uint[] items = [.. Enumerable.Range(0, 24).Select(_ => r.ReadU32())];
            uint[] skills = [r.ReadU32(), r.ReadU32(), r.ReadU32(), r.ReadU32()];
            r.ReadU32();
            byte points = r.ReadU8();
            MagLoadout[] loadouts = [.. Enumerable.Range(0, 5).Select(_ => MagLoadout.Read(r))];
            r.ReadU8(); r.ReadU32(); r.ReadU32(); r.ReadU32();
            for (int i = 0; i < 4; i++) r.ReadU8();               // head, voice, nominations, matches
            byte respecCount = r.ReadU8();
            uint respecPoints = r.ReadU32();
            r.ReadU32(); r.ReadU8(); r.ReadBool();
            byte lastUsed = r.ReadU8();
            r.ReadBytes(36);
            r.ReadU8(); r.ReadU8();
            return new WireRecord(items, skills, points, loadouts, respecCount, respecPoints, lastUsed);
        }
    }

    private async Task LoginAsync(MediusTestClient client, string onlineId, ulong userId)
    {
        await client.ConnectAsync(fixture.MapsPort);
        await client.HandshakeAsync(LobbyFlowFixture.MagAppId);
        byte[] ticket = PsnTicketTests.BuildTicket(onlineId, userId);
        SendTicketChunk(client, ticket, last: true, transId: 1);
        ZipperBitReader auth = await ExpectPluginAsync(client, MagIds.Retail.NpAuthenticateResponse);
        MapsEnvelope.Read(auth);
        Assert.True(auth.ReadBool());
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
            w.WriteU16(MagIds.Retail.PatchRequest);
            w.WriteI32(0);
            w.WriteI32(0);
            w.WriteString("ABmagprod370048");
            w.WriteString("english");
        });
        ZipperBitReader response = await ExpectPluginAsync(client, MagIds.Retail.PatchResponse!.Value);
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
        byte[] stream = Assert.Single(MapsWriters.PatchReply(MagClientVersion.Retail, [new MapsPatchFile { Name = "patch_001.psarc", Kind = 2, Size = 1234 }]));
        ZipperBitReader reader = new(stream);
        Assert.Equal(MagIds.Retail.PatchResponse, reader.ReadU16());
        Assert.Equal(1, reader.ReadU16());
        Assert.Equal("patch_001.psarc", reader.ReadString());
        Assert.Equal(2, reader.ReadU8());
        Assert.Equal(1234u, reader.ReadU32());
    }

    private static void SendTicketChunk(MediusTestClient client, byte[] chunk, bool last, int transId) =>
        SendPlugin(client, w =>
        {
            w.WriteU16(MagIds.Retail.NpAuthenticateRequest);
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
