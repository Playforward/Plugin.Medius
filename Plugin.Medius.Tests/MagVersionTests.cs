using Plugin.Medius.Extensions.Mag;
using Plugin.Medius.Extensions.Zipper;

namespace Plugin.Medius.Tests;

/// <summary>
/// The per-build tables (message ids from tools/re/msgtypes.py, game types,
/// skill tables) and the layouts that differ between 1.00 and 2.12.
/// </summary>
public sealed class MagVersionTests
{
    private static readonly MagClientVersion Retail = MagClientVersion.Retail;
    private static readonly MagClientVersion Patch = MagClientVersion.Patch212;

    [Fact]
    public void Message_table_has_both_builds()
    {
        Assert.Equal([MagProtocols.Retail, MagProtocols.Patch212], MagMessageTable.Builds.Select(b => b.Protocol).Order());
        Assert.Equal("ABmagprod370048", MagMessageTable.ForProtocol(MagProtocols.Retail)!.Build);
        Assert.Equal("ABmagprod493603", MagMessageTable.ForProtocol(MagProtocols.Patch212)!.Build);
        Assert.Same(Patch, MagClientVersion.ForProtocol(1793));
        Assert.Null(MagClientVersion.ForProtocol(1725));
        Assert.Equal(Retail, new MagSettings { Protocol = 1725 }.Version);    // unknown: 1.00's
    }

    /// <summary>The 1.00 ids the lobby and game server were first written with, now read from the code [C, live].</summary>
    [Fact]
    public void Retail_ids_match_the_ones_seen_live()
    {
        MagMessageIds ids = Retail.Messages;
        Assert.Equal(
            [0x01, 0x02, 0x03, 0x2E, 0x2F, 0x30, 0x31, 0x34, 0x35, 0x36, 0x37, 0x38, 0x12, 0x13, 0x15, 0x39, 0x2D],
            new int[] { ids.Hello, ids.ProtocolInfo, ids.KeepAlive, ids.AccountLoginRequest, ids.AccountLoginResponse,
                ids.AccountLogoutRequest, ids.AccountLogoutResponse, ids.NpAuthenticateRequest, ids.NpAuthenticateResponse,
                ids.MapsHello, ids.UniverseListRequest, ids.UniverseListResponse, ids.JoinGameRequest, ids.JoinGameResponse,
                ids.CancelJoinRequest, ids.JoinGameConfirm, ids.TrainingCompletedRequest });
        Assert.Equal([0x236, 0x237, 0x238, 0x239, 0x23A, 0x23B, 0x23C, 0x23D, 0x23E, 0x23F, 0x24B, 0x24C],
            new int[] { ids.CharacterListRequest, ids.CharacterListResponse, ids.CharacterDataRequest, ids.CharacterDataResponse,
                ids.CreateCharacterRequest, ids.CreateCharacterResponse, ids.DeleteCharacterRequest, ids.DeleteCharacterResponse,
                ids.NewsEulaRequest, ids.NewsEulaResponse, ids.ServerStatusRequest, ids.ServerStatusResponse });
        Assert.Equal([0x21F, 0x220, 0x221, 0x222, 0x250, 0x251, 0x252, 0x254, 0x255, 0x25A, 0x25B, 0x25C],
            new int[] { ids.LoadoutUpdateRequest, ids.LoadoutUpdateResponse, ids.LoadoutDeleteRequest, ids.LoadoutDeleteResponse,
                ids.PurchaseSkillRequest, ids.PurchaseSkillResponse, ids.SelectLoadout, ids.SkillReSpecRequest,
                ids.SkillReSpecResponse, ids.FactionReSpecRequest, ids.VeteranReSpecRequest, ids.CharacterReSpecResponse });
        Assert.Equal([0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0B, 0x0C, 0x11],
            new int[] { ids.ClientClaimReservation, ids.ClientGameInfo, ids.ClientReady, ids.ClientEnterGame, ids.FieldUpdate,
                ids.CorrectionAck, ids.NetTimeRequest, ids.NetTimeResponse, ids.PluginId });
        Assert.Equal([0x20F, 0x210, 0x211, 0x213, 0x219, 0x208, 0x209],
            new int[] { ids.Hit, ids.FireGrenade, ids.ConfirmRespawn, ids.CancelRespawn, ids.CharacterSpawnSelection, ids.PingRequest, ids.PingResponse });
        Assert.Equal((ushort)0x1F5, ids.PatchRequest);
        Assert.Equal((ushort)0x1F6, ids.PatchResponse);
        Assert.Null(ids.PatchSettings);
        Assert.Null(ids.GameInfo);
    }

    /// <summary>2.12 moved the account messages up two and the game messages up 0x12..0x18 (constructors in the 2.12 mage_g) [C].</summary>
    [Fact]
    public void Patch212_ids_are_renumbered()
    {
        MagMessageIds ids = Patch.Messages;
        Assert.Equal([0x30, 0x31, 0x32, 0x33, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x12, 0x13, 0x15, 0x2D],
            new int[] { ids.AccountLoginRequest, ids.AccountLoginResponse, ids.AccountLogoutRequest, ids.AccountLogoutResponse,
                ids.NpAuthenticateRequest, ids.NpAuthenticateResponse, ids.MapsHello, ids.UniverseListRequest,
                ids.UniverseListResponse, ids.JoinGameConfirm, ids.JoinGameRequest, ids.JoinGameResponse, ids.CancelJoinRequest,
                ids.TrainingCompletedRequest });
        Assert.Equal([0x24A, 0x24B, 0x24E, 0x24F, 0x263, 0x264, 0x268, 0x26A, 0x272, 0x274],
            new int[] { ids.CharacterListRequest, ids.CharacterListResponse, ids.CreateCharacterRequest, ids.CreateCharacterResponse,
                ids.ServerStatusRequest, ids.ServerStatusResponse, ids.PurchaseSkillRequest, ids.SelectLoadout,
                ids.FactionReSpecRequest, ids.CharacterReSpecResponse });
        Assert.Equal([0x221, 0x222, 0x223, 0x225, 0x22B, 0x21A, 0x21B],
            new int[] { ids.Hit, ids.FireGrenade, ids.ConfirmRespawn, ids.CancelRespawn, ids.CharacterSpawnSelection, ids.PingRequest, ids.PingResponse });
        // The kernel block is unchanged (the game dispatcher handles the same cases, 0x153184).
        Assert.Equal([0x04, 0x08, 0x0B, 0x11], new int[] { ids.ClientClaimReservation, ids.FieldUpdate, ids.NetTimeRequest, ids.PluginId });
        Assert.Equal((ushort)0x05, ids.ClientReservationResponse);
        Assert.Equal((ushort)0x20C, ids.GameInfo);
        Assert.Null(ids.PatchResponse);
        Assert.Equal((ushort)0x1F6, ids.PatchSettings);
        Assert.Equal((ushort)0x1F7, ids.PatchUrl);
    }

    [Fact]
    public void Game_type_numbers_follow_the_build()
    {
        Assert.Equal(3u, Retail.GameTypes.IdOf(MagGameTypes.Suppression));
        Assert.Equal(5u, Patch.GameTypes.IdOf(MagGameTypes.Suppression));
        Assert.Equal(MagGameTypes.Escalation, Patch.GameTypes.NameOf(1));
        Assert.Null(Retail.GameTypes.IdOf(MagGameTypes.Interdiction));
    }

    [Fact]
    public void Each_build_has_its_skill_table()
    {
        MagSkillTable retail = MagSkillTable.Embedded(Retail), patch = MagSkillTable.Embedded(Patch);
        Assert.Equal((98, 60), (retail.Skills.Count, retail.MaxLevel));
        Assert.Equal((160, 70), (patch.Skills.Count, patch.MaxLevel));
        Assert.Equal(new uint[] { 1000, 3000, 5000 }, patch.ReSpecPoints);
        Assert.Empty(patch.Tiers);
        Assert.Equal([0], patch.Skills.Where(s => s.IsStarting).Select(s => s.Id));
        Assert.Equal("SKILL_ID_BasicDefault", patch.Skills[0].SkillId);
        Assert.Equal(160, MagCharacter.SkillCount);
    }

    /// <summary>CanPurchaseSkill 0x6fa084: the parent first, no tiers; the free skill is BasicDefault.</summary>
    [Fact]
    public void Patch212_skills_need_their_parent()
    {
        MagSkillTable table = MagSkillTable.Embedded(Patch);
        MagRules rules = new(new MagSettings { Protocol = MagProtocols.Patch212 }, table);
        Assert.Equal(70, rules.MaxLevel);
        MagCharacter soldier = new() { Faction = 0, SkillPoints = 10 };
        Assert.True(soldier.GrantStartingSkills(rules));
        Assert.True(soldier.HasSkill(0));

        MagSkill child = table.Skills.First(s => s.Parent is { } p && table.Skills[p].Parent is null && MagSkillTable.IsAvailable(s, 0));
        Assert.False(soldier.TryPurchaseSkill(child.Id, rules, out string reason));
        Assert.Contains("parent", reason);
        Assert.True(soldier.TryPurchaseSkill(child.Parent!.Value, rules, out reason), reason);
        Assert.True(soldier.TryPurchaseSkill(child.Id, rules, out reason), reason);
        Assert.Equal(10 - table.Skills[child.Parent.Value].Cost - child.Cost, soldier.SkillPoints);
    }

    /// <summary>2.12's record adds 6 × u16 and 5 × u32 after m_flags (0x70e914); its summary two bools (0x70e278).</summary>
    [Fact]
    public void Patch212_record_and_summary_have_their_tails()
    {
        MagCharacter soldier = new() { Name = "Tail", Unknown22A = [1, 2, 3, 4, 5, 6], Unknown238 = [7, 8, 9, 10, 11] };
        Assert.Equal(6 * 16 + 5 * 32, Bits(w => soldier.WriteRecord(w, Patch)) - Bits(w => soldier.WriteRecord(w, Retail)));
        Assert.Equal(2, Bits(w => soldier.WriteSummary(w, Patch)) - Bits(w => soldier.WriteSummary(w, Retail)));

        ZipperBitWriter writer = new();
        soldier.WriteRecord(writer, Patch);
        ZipperBitReader reader = new(writer.ToArray());
        for (int skip = Bits(w => soldier.WriteRecord(w, Retail)); skip > 0; skip -= Math.Min(skip, 32))
            reader.ReadBits(Math.Min(skip, 32));
        Assert.Equal([1, 2, 3, 4, 5, 6], Enumerable.Range(0, 6).Select(_ => (int)reader.ReadU16()));
        Assert.Equal([7u, 8u, 9u, 10u, 11u], Enumerable.Range(0, 5).Select(_ => reader.ReadU32()));
    }

    [Fact]
    public void Character_list_response_has_no_flag_byte_in_2_12()
    {
        MagCharacter soldier = new() { Name = "Listed" };
        int retail = MapsWriters.CharacterListResponse(Retail, new MapsEnvelope(), true, 1, [new MagCharacterSlot(soldier)]).Length;
        byte[] patch = MapsWriters.CharacterListResponse(Patch, new MapsEnvelope(), true, 1, [new MagCharacterSlot(soldier)]);
        ZipperBitReader reader = new(patch);
        Assert.Equal(Patch.Messages.CharacterListResponse, reader.ReadU16());
        MapsEnvelope.Read(reader);
        Assert.True(reader.ReadBool());
        Assert.Equal(1, reader.ReadU8());                       // the count, straight after the success bit
        Assert.Equal("Listed", MagCharacter.ReadShortString(reader));
        Assert.True(patch.Length >= retail - 1);
    }

    /// <summary>
    /// 2.12's join names game types as a bitset (kind 1, bit n = game type n):
    /// its Suppression queue sent 32 [live]. 1.00 sends the number itself.
    /// </summary>
    [Fact]
    public void Join_request_game_types_are_a_mask_in_2_12()
    {
        MagGameTypeTable patch = Patch.GameTypes, retail = Retail.GameTypes;
        Assert.Equal([MagGameTypes.Suppression], new MagJoinGameRequest(1, 32, 0, 0, 0).GameTypeNames(Patch, patch));
        uint both = (1u << (int)patch.IdOf(MagGameTypes.Sabotage)!.Value) | 32;
        Assert.Equal([MagGameTypes.Sabotage, MagGameTypes.Suppression], new MagJoinGameRequest(1, both, 0, 0, 0).GameTypeNames(Patch, patch));
        Assert.Null(new MagJoinGameRequest(3, 32, 0, 0, 0).GameTypeNames(Patch, patch));   // another kind of join
        Assert.Null(new MagJoinGameRequest(1, 0, 0, 0, 0).GameTypeNames(Patch, patch));
        Assert.Null(new MagJoinGameRequest(1, 1u << 7, 0, 0, 0).GameTypeNames(Patch, patch));

        uint suppression = retail.IdOf(MagGameTypes.Suppression)!.Value;
        Assert.Equal([MagGameTypes.Suppression], new MagJoinGameRequest(1, suppression, 0, 0, 0).GameTypeNames(Retail, retail));
    }

    /// <summary>
    /// 2.12 lists slots: an account without a soldier still gets an empty,
    /// unlocked slot (character id 0, +0x80 clear), or the client offers only
    /// to buy one. 1.00 lists no entry for it.
    /// </summary>
    [Fact]
    public void Empty_slot_is_listed_in_2_12_only()
    {
        MagCharacterSlot empty = new(null);
        ZipperBitReader reader = new(MapsWriters.CharacterListResponse(Patch, new MapsEnvelope(), true, 1, [empty]));
        reader.ReadU16();
        MapsEnvelope.Read(reader);
        Assert.True(reader.ReadBool());
        Assert.Equal(1, reader.ReadU8());
        Assert.Equal("", MagCharacter.ReadShortString(reader));
        Assert.Equal(0u, reader.ReadU32());                     // character id 0: CharacterListExists is false

        ZipperBitWriter summary = new();
        empty.Write(summary, Patch);
        ZipperBitReader tail = new(summary.ToArray());
        int bits = Bits(w => empty.Write(w, Patch));
        for (int skip = bits - 2; skip > 0; skip -= Math.Min(skip, 32))
            tail.ReadBits(Math.Min(skip, 32));
        Assert.False(tail.ReadBool());                          // +0x80: unlocked
        Assert.False(tail.ReadBool());

        ZipperBitReader retail = new(MapsWriters.CharacterListResponse(Retail, new MapsEnvelope(), true, 1, [empty]));
        retail.ReadU16();
        MapsEnvelope.Read(retail);
        retail.ReadBool();
        Assert.Equal(1, retail.ReadU8());                       // the flag byte
        Assert.Equal(0, retail.ReadU8());                       // no soldiers
    }

    /// <summary>2.12's CreateCharacterRequest ends with a bool after the NpId (marshal 0x7101e4).</summary>
    [Fact]
    public void Patch212_create_request_has_a_trailing_bool()
    {
        ZipperBitWriter writer = new();
        MagCharacter.WriteShortString(writer, "Fresh");
        writer.WriteU8(2);
        writer.WriteU8(3);
        writer.WriteU8(4);
        writer.WriteU32(7);
        for (int i = 0; i < 36; i++)
            writer.WriteU8(0);
        writer.WriteBool(true);
        MagCharacter created = MagCharacter.ReadCreateRequest(new ZipperBitReader(writer.ToArray()), Patch);
        Assert.Equal(("Fresh", (sbyte)2, (byte)3, (byte)4, 7u), (created.Name, created.Faction, created.HeadId, created.VoiceId, created.UniverseId));
        Assert.True(created.CreateRequestUnknownB0);
    }

    /// <summary>The 2.12 launcher's patch check ends on a PatchURL with isLast set (handler EBOOT 0x123e0).</summary>
    [Fact]
    public void Patch212_patch_reply_is_settings_then_a_last_url()
    {
        IReadOnlyList<byte[]> reply = MapsWriters.PatchReply(Patch, []);
        Assert.Equal(2, reply.Count);
        ZipperBitReader settings = new(reply[0]);
        Assert.Equal((ushort)0x1F6, settings.ReadU16());
        for (int i = 0; i < 3; i++)
            Assert.Equal(-1, settings.ReadI32());
        Assert.Equal(0x2000u, settings.ReadU32());
        ZipperBitReader url = new(reply[1]);
        Assert.Equal((ushort)0x1F7, url.ReadU16());
        Assert.Equal("", url.ReadString());
        url.ReadU8();
        url.ReadU32();
        Assert.True(url.ReadBool());

        ZipperBitWriter request = new();
        request.WriteU16(2);
        request.WriteI32(1);
        request.WriteI32(12);
        request.WriteString("ABmagprod493603");
        request.WriteString("english");
        MapsPatchRequest read = MapsPatchRequest.Read(new ZipperBitReader(request.ToArray()), Patch);
        Assert.Equal([1, 12], read.Levels);
        Assert.Equal("ABmagprod493603", read.Build);
    }

    /// <summary>ServerStatusResponse: 4 × 3 queues in 1.00 (0x8c7894), 6 × 3 and the new blocks in 2.12 (0x71396c).</summary>
    [Fact]
    public void Server_status_has_a_row_per_queued_game_type()
    {
        int retail = Bits(w => MagServerStatus.Uniform(Retail, true).Write(w));
        int patch = Bits(w => MagServerStatus.Uniform(Patch, true).Write(w));
        Assert.Equal(12 * 32 * 3 + 9 * 8 + 4 * 32 + 12, retail);
        Assert.Equal(18 * 32 * 3 + 15 * 8 + 12 * 32 + 6 * (16 + 1 + 3) + 3 + 5 * 32 + 2 + 2 * 32 + 6 * 32 + 32, patch);
    }

    [Fact]
    public void Join_game_confirm_layouts()
    {
        Assert.Equal(32 + 8 + 8, Bits(w => new MagJoinGameConfirm(5).Write(w, Retail)));
        Assert.Equal(32 + 32 + 8 + 1 + 32, Bits(w => new MagJoinGameConfirm(5).Write(w, Patch)));
    }

    private static int Bits(Action<ZipperBitWriter> write)
    {
        ZipperBitWriter writer = new();
        write(writer);
        return writer.BitLength;
    }
}
