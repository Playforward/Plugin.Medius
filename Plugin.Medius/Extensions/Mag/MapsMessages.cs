using Plugin.Medius.Extensions.Zipper;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// MAG's MAPS plugin messages. Their ids depend on the client's build and
/// come from <see cref="MagMessageIds"/> (generated from each build's code);
/// the readers and writers here take the <see cref="MagClientVersion"/> where
/// a layout differs between builds. Addresses are the retail 1.00 mage_g's
/// unless marked 2.12.
/// </summary>
/// <summary>
/// The fields every MAPS client message starts with after its type
/// (NetPluginClientMessage&lt;&gt;: m_rsaKey, then m_transID).
/// </summary>
public sealed class MapsEnvelope
{
    /// <summary>16 words, least significant first, as SCE-RT keeps RSA keys.</summary>
    public uint[] RsaKey { get; set; } = new uint[16];
    public int TransId { get; set; }

    public static MapsEnvelope Read(ZipperBitReader reader)
    {
        MapsEnvelope envelope = new();
        for (int i = 0; i < 16; i++)
            envelope.RsaKey[i] = reader.ReadU32();
        envelope.TransId = reader.ReadI32();
        return envelope;
    }

    public void Write(ZipperBitWriter writer)
    {
        foreach (uint word in RsaKey)
            writer.WriteU32(word);
        writer.WriteI32(TransId);
    }

    public static uint[] KeyWords(Org.BouncyCastle.Math.BigInteger modulus)
    {
        uint[] words = new uint[16];
        byte[] bytes = modulus.ToByteArrayUnsigned();
        for (int i = 0; i < 16; i++)
        {
            int end = bytes.Length - i * 4;
            uint word = 0;
            for (int j = Math.Max(0, end - 4); j < end; j++)
                word = (word << 8) | bytes[j];
            words[i] = end > 0 ? word : 0;
        }
        return words;
    }
}

/// <summary>NpAuthenticateRequest: one chunk of the player's PSN ticket.</summary>
public sealed class MapsNpAuthenticateChunk
{
    public required MapsEnvelope Envelope { get; init; }
    public required byte[] Chunk { get; init; }
    /// <summary>The u16 after the chunk (mage_g marshals it from +0x3e2); its meaning is not settled yet.</summary>
    public ushort Trailer { get; init; }
    public bool IsLast { get; init; }

    /// <summary>
    /// The chunk buffer is always marshalled whole: 900 bytes, of which the
    /// u16 before it says how many are ticket. A retail request with a
    /// 256-byte RPCN ticket was 975 bytes, which only adds up this way.
    /// </summary>
    public const int ChunkBufferLength = 900;

    public static MapsNpAuthenticateChunk Read(ZipperBitReader reader)
    {
        MapsEnvelope envelope = MapsEnvelope.Read(reader);
        int length = Math.Min((int)reader.ReadU16(), ChunkBufferLength);
        byte[] chunk = reader.ReadBytes(ChunkBufferLength)[..length];
        ushort trailer = reader.ReadU16();
        bool last = reader.ReadBool();
        return new MapsNpAuthenticateChunk { Envelope = envelope, Chunk = chunk, Trailer = trailer, IsLast = last };
    }
}

/// <summary>
/// PatchRequest (0x1F5 in both builds), the launcher's patch check: the
/// patch levels it already has on the HDD, its build string and language.
/// <list type="bullet">
/// <item>1.00 (marshal EBOOT 0x8abec): the highest and the initial level as
/// two i32, both 0 when it holds fewer than two patch entries.</item>
/// <item>2.12 (marshal EBOOT 0x5a834): a u16 count and that many u32
/// levels, one per installed patch [C].</item>
/// </list>
/// <see cref="Levels"/> holds them in wire order either way.
/// </summary>
public sealed class MapsPatchRequest
{
    public IReadOnlyList<int> Levels { get; init; } = [];
    public string Build { get; init; } = "";
    public string Language { get; init; } = "";

    public static MapsPatchRequest Read(ZipperBitReader reader, MagClientVersion version)
    {
        List<int> levels = [];
        if (version.Format == MagWireFormat.Retail100)
        {
            levels.Add(reader.ReadI32());   // highest
            levels.Add(reader.ReadI32());   // initial
        }
        else
        {
            int count = reader.ReadU16();
            for (int i = 0; i < count && reader.BitsLeft >= 32; i++)
                levels.Add(reader.ReadI32());
        }
        return new MapsPatchRequest { Levels = levels, Build = reader.ReadString(), Language = reader.ReadString() };
    }
}

/// <summary>
/// FactionReSpecRequest / VeteranReSpecRequest (demarshal 0x8c3720): a u32
/// at +8 that the emulator's handlers never read, then the PMC to move to
/// (+0xc, read as a signed byte by 0x17de30) [C].
/// </summary>
public sealed record MagReSpecRequest(uint Unknown, sbyte Faction)
{
    public static MagReSpecRequest Read(ZipperBitReader reader) => new(reader.ReadU32(), (sbyte)reader.ReadU8());
}

/// <summary>
/// DeleteCharacterRequest (vtable 0x1a5d968, demarshal 0x454b90), after the
/// envelope: the soldier's name (u16-length string, 0xedb998) and its
/// character id (u32 at +0x7c). The lobby builds it from the selected entry
/// of its character list (0x8206c: name at entry+0, id at entry+0x20) [C].
/// Answered with <see cref="MapsWriters.StatusResponse"/>: the envelope and
/// one success bit (demarshal 0x452394) [C].
/// </summary>
public sealed record MagDeleteCharacterRequest(string Name, uint CharacterId)
{
    public static MagDeleteCharacterRequest Read(ZipperBitReader reader) => new(reader.ReadString(), reader.ReadU32());
}

/// <summary>
/// zGame's game types by name: the game creation table's m_gameType
/// (settings/gameproperties.zpr), which is what the server's data is keyed
/// on. The client numbers them differently per version
/// (<see cref="MagGameTypeTable"/>).
/// </summary>
public static class MagGameTypes
{
    /// <summary>Sabotage, 64 players.</summary>
    public const string Sabotage = "GameType64";
    /// <summary>Escalation, 96 players, three PMCs (patch 2.x; its maps were downloadable content).</summary>
    public const string Escalation = "GameType_96_Sabotage";
    /// <summary>Acquisition, 128 players.</summary>
    public const string Acquisition = "GameType128";
    /// <summary>Interdiction, 128 players (patch 2.x; its maps were downloadable content).</summary>
    public const string Interdiction = "GameTypeInterdiction";
    /// <summary>Domination, 256 players.</summary>
    public const string Domination = "GameType256";
    /// <summary>Suppression: in 1.00 a PMC's exercise against itself on its own map.</summary>
    public const string Suppression = "GameTypeFactionExercise";
    /// <summary>A PMC's training level; not in the game creation table.</summary>
    public const string Training = "GameTypeTraining";
}

/// <summary>
/// The client's numbers for the game types: zGame's GameType, registered
/// for its scripts as kGameType_* in this order [C]. The numbers shifted
/// when patches added game types, so the table depends on the client.
/// </summary>
public sealed class MagGameTypeTable(IReadOnlyList<string> names)
{
    /// <summary>Retail 1.00 (protocol 1740; registration at mage_g 0xba408): 64, 128, 256, FactionExercise, Training = 0..4.</summary>
    public static readonly MagGameTypeTable Retail =
        new([MagGameTypes.Sabotage, MagGameTypes.Acquisition, MagGameTypes.Domination, MagGameTypes.Suppression, MagGameTypes.Training]);

    /// <summary>
    /// Patch 2.12 (protocol 1793; registration at mage_g 0x9b1f0, Lua number
    /// constants): 64, 96_Sabotage, 128, Interdiction, 256, FactionExercise,
    /// Training = 0..6.
    /// </summary>
    public static readonly MagGameTypeTable Patch212 =
        new([MagGameTypes.Sabotage, MagGameTypes.Escalation, MagGameTypes.Acquisition, MagGameTypes.Interdiction,
             MagGameTypes.Domination, MagGameTypes.Suppression, MagGameTypes.Training]);

    /// <summary>The table of the client that speaks <paramref name="protocol"/>, if it is one we have read.</summary>
    public static MagGameTypeTable? ForProtocol(uint protocol) => protocol switch
    {
        MagProtocols.Retail => Retail,
        MagProtocols.Patch212 => Patch212,
        _ => null,
    };

    public IReadOnlyList<string> Names { get; } = names;

    /// <summary>The game type the client means by <paramref name="id"/>, or null if it has no such number.</summary>
    public string? NameOf(uint id) => id < Names.Count ? Names[(int)id] : null;

    /// <summary>The client's number for <paramref name="name"/>, or null if this client doesn't have it.</summary>
    public uint? IdOf(string name)
    {
        for (int i = 0; i < Names.Count; i++)
            if (string.Equals(Names[i], name, StringComparison.OrdinalIgnoreCase))
                return (uint)i;
        return null;
    }
}

/// <summary>
/// NetJoinGameRequest (vtable 0x1a4e010, marshal 0x1e8c8c), after the
/// envelope: a 16-byte struct copied in by the constructor (0x1e8e88) and
/// written by 0x12d548 as u8 (+3), u32 (+4), u32 (+8), u8 (+0xc); then a u32
/// (+0x6c, the constructor's third argument) [C]. The u32 at +4 is the
/// client's number for the game type queued for (<see cref="MagGameTypeTable"/>):
/// the lobby's queue status is laid out the same way, four game types by
/// three PMCs [C]; a 1.00 Suppression queue sent 3 [live]. Patch 2.12 writes
/// the same layout (marshal 0x16850c, struct 0xf6a34) [C], but the struct is
/// a tagged value: +0..+3 a kind (only its low byte, +3, is sent) and +4 its
/// value. Its setters: kind 1 (0xf69b8) puts game type n into a bitset
/// (0x139390 sets bit n, n ≤ 6; 0x139350 reads it back as a u32), so +4 is a
/// mask of the game types queued for (2.10's multiqueue); a 2.12 Suppression
/// queue sent kind 1, 32 = bit 5 = FactionExercise [live]. Kinds 3 and 4
/// (0xf6984, 0xf69a8) carry some other u32, kind 5 (0xf6994) none; what they
/// join is not known yet. 1.00 has no such bitset: +4 is the game type number.
/// </summary>
public sealed record MagJoinGameRequest(byte Unknown03, uint GameTypeId, uint Unknown08, byte Unknown0C, uint Unknown6C)
{
    public static MagJoinGameRequest Read(ZipperBitReader reader) => new(
        reader.ReadU8(), reader.ReadU32(), reader.ReadU32(),
        reader.BitsLeft >= 8 ? reader.ReadU8() : (byte)0,
        reader.BitsLeft >= 32 ? reader.ReadU32() : 0);

    /// <summary>2.12's kind for a join by game types (setter 0xf69b8).</summary>
    public const byte KindGameTypes = 1;

    /// <summary>The highest game type bit 2.12's bitset takes (0x139390 ignores larger ones).</summary>
    public const int MaxGameTypeBit = 6;

    /// <summary>
    /// The game types the player queued for, by name, or null if the request
    /// isn't a join by game type or names a number <paramref name="table"/>
    /// doesn't have. 1.00: the one numbered game type. 2.12: kind
    /// <see cref="KindGameTypes"/>, each set bit of the mask, in bit order.
    /// </summary>
    public IReadOnlyList<string>? GameTypeNames(MagClientVersion version, MagGameTypeTable table)
    {
        if (version.Format == MagWireFormat.Retail100)
            return table.NameOf(GameTypeId) is { } name ? [name] : null;
        if (Unknown03 != KindGameTypes || GameTypeId == 0 || GameTypeId >> (MaxGameTypeBit + 1) != 0)
            return null;
        List<string> names = [];
        for (int bit = 0; bit <= MaxGameTypeBit; bit++)
        {
            if ((GameTypeId & (1u << bit)) == 0)
                continue;
            if (table.NameOf((uint)bit) is not { } name)
                return null;
            names.Add(name);
        }
        return names;
    }

    public void Write(ZipperBitWriter writer)
    {
        writer.WriteU8(Unknown03);
        writer.WriteU32(GameTypeId);
        writer.WriteU32(Unknown08);
        writer.WriteU8(Unknown0C);
        writer.WriteU32(Unknown6C);
    }
}

/// <summary>One file the launcher is told to fetch over SCE-RT DLS.</summary>
public sealed class MapsPatchFile
{
    public string Name { get; init; } = "";
    /// <summary>Taken to be unknown/generic/psarc (0/1/2); a psarc reserves three times its size on the HDD.</summary>
    public byte Kind { get; init; }
    public uint Size { get; init; }

    /// <summary>name (u16-length string), kind u8, size u32 (1.00 inside PatchResponse; 2.12 PatchURL's 0x5a714) [C].</summary>
    public void Write(ZipperBitWriter writer)
    {
        writer.WriteString(Name);
        writer.WriteU8(Kind);
        writer.WriteU32(Size);
    }
}

/// <summary>
/// The DLS settings the launcher copies into its cellDls activate
/// parameters (EBOOT 0x67fb4): three i32, a u32, a u8 and six i32. The
/// defaults are 1.00's message constructor's (EBOOT 0x8aa38). 2.12 sends
/// the same fields as NetMessagePatchSettings (demarshal EBOOT 0x5a50c:
/// three 32-bit, one 32-bit, one 8-bit, six 32-bit) [C].
/// </summary>
public sealed class MapsPatchSettings
{
    public int[] Unknown08 { get; init; } = [-1, -1, -1];
    public uint Unknown14 { get; init; } = 0x2000;
    public byte Unknown18 { get; init; }
    public int[] Unknown1C { get; init; } = [-1, -1, -1, -1, -1, -1];

    public void Write(ZipperBitWriter writer)
    {
        foreach (int value in Unknown08)
            writer.WriteI32(value);
        writer.WriteU32(Unknown14);
        writer.WriteU8(Unknown18);
        foreach (int value in Unknown1C)
            writer.WriteI32(value);
    }
}

/// <summary>
/// What the lobby's server status shows: per game type (rows, by the
/// client's game type number) and PMC (columns: Valor, Raven, SVER). 1.00
/// has four rows (Sabotage, Acquisition, Domination, Suppression), 2.12 six
/// (adding Escalation and Interdiction). Fields with a placeholder name are
/// read by the client but not understood yet.
/// </summary>
public sealed class MagServerStatus
{
    public const int Factions = 3;

    public int Rows { get; }
    /// <summary>
    /// Each PMC's players online, per game type: FactionNumPlayersOnline, the
    /// deploy screen's PLAYER COUNT (2.12 client +0x765c, copied by the
    /// status handler 0x6d174) [C].
    /// </summary>
    public uint[,] PlayersOnline { get; }
    /// <summary>
    /// Each PMC's Shadow War score per game type, 0..100: GametypeFactionScore
    /// (client +0x76a4, getter 0xa6594), the frame of the deploy screen's
    /// CURRENT SCORE bars (101 frames) [C].
    /// </summary>
    public uint[,] WarScores { get; }
    /// <summary>
    /// Shadow War contracts, signed bytes (1.00 reader 0x3abc90: 3×3; 2.12
    /// 0xb89b54: 5×3): per game type with contracts (the client's game type
    /// number), the PMC holding each contract (-1 none). The client keeps them
    /// as i32s at +0x76ec; GametypeFactionContractOwner (0x33a794) and
    /// GametypeLeader (0x33ae24) read them [C].
    /// </summary>
    public sbyte[,] Contracts { get; }
    public uint[,] QueueSizes { get; }
    /// <summary>One u32 per game type after the queue sizes (1.00 +0x114; called shadow war scores before, unconfirmed).</summary>
    public uint[] PerGameType114 { get; }
    /// <summary>2.12 only: a second u32 per game type (+0x18c).</summary>
    public uint[] PerGameType18C { get; }
    /// <summary>Whether each queue is open (1.00 bits at +0x124, 0x18c220; 2.12 the queue controls' first 18 bools).</summary>
    public bool[,] QueueOpen { get; }

    // 2.12's queue controls (+0x1a4, reader 0xb89b64): per game type a u16
    // and a bool besides the three open flags, then three more bools.
    public ushort[] QueueUnknown12 { get; }
    public bool[] QueueUnknown1F { get; }
    public bool QueueUnknown1E { get; set; }
    public bool QueueUnknown25 { get; set; }
    public bool QueueUnknown26 { get; set; }

    // 2.12's block at +0x1cc (reader 0xb89b74) and the u32 at +0x204.
    public uint[] Unknown1CC { get; } = new uint[5];
    public bool Unknown1E0 { get; set; }
    public bool Unknown1E1 { get; set; }
    /// <summary>Two 32-bit values (reader 0xf6fe4, possibly floats).</summary>
    public uint[] Unknown1E4 { get; } = new uint[2];
    public uint[] Unknown1EC { get; } = new uint[6];
    public uint Unknown204 { get; set; }

    public MagServerStatus(MagClientVersion version)
    {
        Rows = version.Format == MagWireFormat.Retail100 ? 4 : 6;
        PlayersOnline = new uint[Rows, Factions];
        WarScores = new uint[Rows, Factions];
        Contracts = new sbyte[version.Format == MagWireFormat.Retail100 ? 3 : 5, Factions];
        QueueSizes = new uint[Rows, Factions];
        PerGameType114 = new uint[Rows];
        PerGameType18C = new uint[version.Format == MagWireFormat.Retail100 ? 0 : Rows];
        QueueOpen = new bool[Rows, Factions];
        QueueUnknown12 = new ushort[version.Format == MagWireFormat.Retail100 ? 0 : Rows];
        QueueUnknown1F = new bool[version.Format == MagWireFormat.Retail100 ? 0 : Rows];
    }

    /// <summary>Every queue open or closed; the rest zero.</summary>
    public static MagServerStatus Uniform(MagClientVersion version, bool queuesOpen)
    {
        MagServerStatus status = new(version);
        for (int row = 0; row < status.Rows; row++)
            for (int faction = 0; faction < Factions; faction++)
                status.QueueOpen[row, faction] = queuesOpen;
        return status;
    }

    /// <summary>
    /// The body after the envelope, in the order the client reads it (1.00
    /// demarshal 0x8c7894; 2.12 0x71396c) [C].
    /// </summary>
    public void Write(ZipperBitWriter writer)
    {
        WriteGrid(writer, PlayersOnline);
        WriteGrid(writer, WarScores);
        for (int i = 0; i < Contracts.GetLength(0); i++)
            for (int j = 0; j < Factions; j++)
                writer.WriteU8((byte)Contracts[i, j]);
        WriteGrid(writer, QueueSizes);
        foreach (uint value in PerGameType114)
            writer.WriteU32(value);
        foreach (uint value in PerGameType18C)
            writer.WriteU32(value);
        if (QueueUnknown12.Length == 0)
        {
            foreach (bool open in QueueOpen)
                writer.WriteBool(open);
            return;
        }
        for (int row = 0; row < Rows; row++)
        {
            writer.WriteU16(QueueUnknown12[row]);
            writer.WriteBool(QueueUnknown1F[row]);
            for (int faction = 0; faction < Factions; faction++)
                writer.WriteBool(QueueOpen[row, faction]);
        }
        writer.WriteBool(QueueUnknown1E);
        writer.WriteBool(QueueUnknown25);
        writer.WriteBool(QueueUnknown26);
        foreach (uint value in Unknown1CC)
            writer.WriteU32(value);
        writer.WriteBool(Unknown1E0);
        writer.WriteBool(Unknown1E1);
        foreach (uint value in Unknown1E4)
            writer.WriteU32(value);
        foreach (uint value in Unknown1EC)
            writer.WriteU32(value);
        writer.WriteU32(Unknown204);
    }

    private static void WriteGrid(ZipperBitWriter writer, uint[,] grid)
    {
        foreach (uint value in grid)
            writer.WriteU32(value);
    }
}

/// <summary>
/// NetMessageJoinGameConfirm, sent on the lobby connection once the game
/// server has the player (a server-to-client plugin message: no key words).
/// <list type="bullet">
/// <item>1.00 (0x39; demarshal 0xef9280, handler 0x1c4158): transID, a
/// signed enum at +0x18 (0x180b48), a u8 enum at +0x1c (0x15504c), the
/// result (0 = success) [C].</item>
/// <item>2.12 (0x3B; demarshal 0xc27de8): transID, 32 bits at +0x18 (0x139220,
/// kept as two u16), the signed enum at +0x1c (0x1119b8, the same reader
/// shape as 1.00's +0x18), a bool at +0x20 and a u32 at +0x24 [C]. The
/// 2.12 client's handler (0x156f78) only copies the +0x18 bits to a global
/// (+0xdc/+0xde); which field, if any, still means "result" is a [guess].</item>
/// </list>
/// </summary>
public sealed record MagJoinGameConfirm(int TransId, sbyte Flag = 1, byte Result = 0, uint Unknown18 = 0, bool Unknown20 = false, uint Unknown24 = 0)
{
    public void Write(ZipperBitWriter writer, MagClientVersion version)
    {
        writer.WriteI32(TransId);
        if (version.Format == MagWireFormat.Retail100)
        {
            writer.WriteU8((byte)Flag);
            writer.WriteU8(Result);
            return;
        }
        writer.WriteU32(Unknown18);
        writer.WriteU8((byte)Flag);
        writer.WriteBool(Unknown20);
        writer.WriteU32(Unknown24);
    }
}

public static class MapsWriters
{
    /// <summary>
    /// NewsEulaResponse (demarshal mage_g 0x455818; 2.12 0x3db33c, same
    /// layout): envelope, done:bit, kind:u8 (0 news, 1 EULA), text:string,
    /// u32. The client appends each response's text to that kind's buffer and
    /// stores the u32 with it (0x4d04cc); it carries on only once both kinds
    /// have arrived with done set (flags +0x24c/+0x24d, checked at 0x89f20).
    /// </summary>
    public static byte[] NewsEulaResponse(MagClientVersion version, MapsEnvelope envelope, byte kind, string text, uint versionNumber, bool done)
    {
        ZipperBitWriter writer = Start(version.Messages.NewsEulaResponse);
        envelope.Write(writer);
        writer.WriteBool(done);
        writer.WriteU8(kind);
        writer.WriteString(text);
        writer.WriteU32(versionNumber);
        return writer.ToArray();
    }

    /// <summary>
    /// CharacterListResponse as MAG actually sends it: the subclass
    /// MagNetMessageCharacterListResponse after the envelope and success bit.
    /// 1.00 (demarshal 0x8c1414) reads a byte its constructor sets to 1
    /// (+0x5d) and then a u8 count of summaries; 2.12 (0x70f97c) has no such
    /// byte, just the count and the summaries (0x70e278) [C]. Each summary is
    /// <see cref="MagCharacter.WriteSummary"/>. The base class alone leaves
    /// the client waiting.
    /// 1.00 lists the soldiers. 2.12 lists the account's character slots,
    /// empty ones included (<see cref="MagCharacterSlot"/>): with no entries
    /// the client has no slot to create a soldier in.
    /// </summary>
    public static byte[] CharacterListResponse(MagClientVersion version, MapsEnvelope envelope, bool success, byte flag, IReadOnlyList<MagCharacterSlot> slots)
    {
        ZipperBitWriter writer = Start(version.Messages.CharacterListResponse);
        envelope.Write(writer);
        writer.WriteBool(success);
        if (version.Format == MagWireFormat.Retail100)
        {
            writer.WriteU8(flag);
            List<MagCharacter> characters = slots.Where(s => s.Character is not null).Select(s => s.Character!).ToList();
            writer.WriteU8((byte)characters.Count);
            foreach (MagCharacter character in characters)
                character.WriteSummary(writer, version);
            return writer.ToArray();
        }
        writer.WriteU8((byte)slots.Count);
        foreach (MagCharacterSlot slot in slots)
            slot.Write(writer, version);
        return writer.ToArray();
    }

    /// <summary>
    /// CreateCharacterResponse (demarshal mage_g 0x453a58; 2.12 0x3d59cc):
    /// envelope and a 32-bit result, 0 = kNetSuccess (the first of the
    /// client's kNet* results; kNetNameTaken / kNetNameInvalid follow later).
    /// </summary>
    public static byte[] CreateCharacterResponse(MagClientVersion version, MapsEnvelope envelope, int result)
    {
        ZipperBitWriter writer = Start(version.Messages.CreateCharacterResponse);
        envelope.Write(writer);
        writer.WriteI32(result);
        return writer.ToArray();
    }

    /// <summary>
    /// AccountLoginResponse (demarshal mage_g 0x1e169c, 2.12 0x166244 with
    /// the same reads; field names from its Dump at 0x1e3914): lobbyWorldUID,
    /// accountID, connectionType, addressType, serverIP char[16], serverPort,
    /// worldID, accessKey char[17], sessionKey char[17], result u8 (1 success,
    /// 2 queued), piuid, queuePosition, queueTimeLeft, timestamp (two u32).
    /// </summary>
    public static byte[] AccountLoginResponse(MagClientVersion version, MapsEnvelope envelope, MagLobbyLogin login)
    {
        ZipperBitWriter writer = Start(version.Messages.AccountLoginResponse);
        envelope.Write(writer);
        writer.WriteU32(login.LobbyWorldUid);
        writer.WriteI32(login.AccountId);
        writer.WriteU16(login.ConnectionType);
        writer.WriteU16(login.AddressType);
        WriteFixed(writer, login.ServerIp, 16);
        writer.WriteU16(login.ServerPort);
        writer.WriteU32(login.WorldId);
        WriteFixed(writer, login.AccessKey, 17);
        WriteFixed(writer, login.SessionKey, 17);
        writer.WriteU8(login.Result);
        writer.WriteU32(login.Piuid);
        writer.WriteU32(0);      // queue position
        writer.WriteU32(0);      // queue time left
        ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        writer.WriteU32((uint)(now >> 32));
        writer.WriteU32((uint)now);
        return writer.ToArray();
    }

    /// <summary>
    /// NetJoinGameResponse (0x13 in both builds; demarshal mage_g 0x1e85f8,
    /// 2.12 0x167e78 with the same reads; the client's handler is in
    /// NetClientImplScert at 0x1c86a4) [C]: envelope, a result byte (0 =
    /// success; 1-7 pick the client's join error strings), a game id the
    /// client keeps, the game server's connection info (connection type,
    /// address type, IP char[16], port, world id, access key char[17]) and
    /// the piuid ("JoinGameResponse; piuid=%d").
    /// </summary>
    public static byte[] JoinGameResponse(MagClientVersion version, MapsEnvelope envelope, byte result, uint gameId, ushort connectionType, ushort addressType,
        string serverIp, ushort serverPort, uint worldId, string accessKey, uint piuid)
    {
        ZipperBitWriter writer = Start(version.Messages.JoinGameResponse);
        envelope.Write(writer);
        writer.WriteU8(result);
        writer.WriteU32(gameId);
        writer.WriteU16(connectionType);
        writer.WriteU16(addressType);
        WriteFixed(writer, serverIp, 16);
        writer.WriteU16(serverPort);
        writer.WriteU32(worldId);
        WriteFixed(writer, accessKey, 17);
        writer.WriteU32(piuid);
        return writer.ToArray();
    }

    /// <summary>NetMessageJoinGameConfirm in the client's layout (<see cref="MagJoinGameConfirm"/>).</summary>
    public static byte[] JoinGameConfirm(MagClientVersion version, MagJoinGameConfirm confirm)
    {
        ZipperBitWriter writer = Start(version.Messages.JoinGameConfirm);
        confirm.Write(writer, version);
        return writer.ToArray();
    }

    private static void WriteFixed(ZipperBitWriter writer, string text, int length)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text ?? "");
        for (int i = 0; i < length; i++)
            writer.WriteU8(i < bytes.Length && i < length - 1 ? bytes[i] : (byte)0);
    }

    /// <summary>
    /// CharacterDataResponse as MAG sends it (MagNetMessageCharacterDataResponse,
    /// demarshal 0x8c0e50; 2.12 0x70f310): the base part (envelope, success
    /// bit, a byte at +0x5d; 0x452634) and the character record.
    /// </summary>
    public static byte[] CharacterDataResponse(MagClientVersion version, MapsEnvelope envelope, bool success, byte index, MagCharacter? character)
    {
        ZipperBitWriter writer = Start(version.Messages.CharacterDataResponse);
        envelope.Write(writer);
        writer.WriteBool(success);
        writer.WriteU8(index);
        (character ?? new MagCharacter()).WriteRecord(writer, version);
        return writer.ToArray();
    }

    /// <summary>
    /// NetMessageCharacterRecord: the soldier's record with nothing around it
    /// (2.12 demarshal 0x7125cc reads only the record, 0x70e914) [C]: sent
    /// after a game so the client shows the experience it earned [guess: what
    /// it is for; the client asked for nothing after leaving a game, live
    /// 2026-11-02, and kept showing the old experience].
    /// </summary>
    public static byte[] CharacterRecord(MagClientVersion version, MagCharacter character)
    {
        ZipperBitWriter writer = Start(version.Messages.CharacterRecord);
        character.WriteRecord(writer, version);
        return writer.ToArray();
    }

    /// <summary>ServerStatusResponse: envelope, then <see cref="MagServerStatus"/> in the client's layout.</summary>
    public static byte[] ServerStatusResponse(MagClientVersion version, MapsEnvelope envelope, MagServerStatus status)
    {
        ZipperBitWriter writer = Start(version.Messages.ServerStatusResponse);
        envelope.Write(writer);
        status.Write(writer);
        return writer.ToArray();
    }

    /// <summary>LoadoutUpdateResponse / LoadoutDeleteResponse (both demarshal 0x451d28; 2.12 0x3d3ab0): just a success bit.</summary>
    public static byte[] GameStatusResponse(ushort type, bool success)
    {
        ZipperBitWriter writer = Start(type);
        writer.WriteBool(success);
        return writer.ToArray();
    }

    /// <summary>PurchaseSkillResponse: success, then the record whether or not the purchase went through (marshal 0x8c6fcc; 2.12 0x7131c4).</summary>
    public static byte[] PurchaseSkillResponse(MagClientVersion version, bool success, MagCharacter? character)
    {
        ZipperBitWriter writer = Start(version.Messages.PurchaseSkillResponse);
        writer.WriteBool(success);
        (character ?? new MagCharacter()).WriteRecord(writer, version);
        return writer.ToArray();
    }

    /// <summary>SkillReSpecResponse: success, and the record only on success (marshal 0x8c468c; 2.12 0x7108dc).</summary>
    public static byte[] SkillReSpecResponse(MagClientVersion version, bool success, MagCharacter? character)
    {
        ZipperBitWriter writer = Start(version.Messages.SkillReSpecResponse);
        writer.WriteBool(success);
        if (success)
            (character ?? new MagCharacter()).WriteRecord(writer, version);
        return writer.ToArray();
    }

    /// <summary>
    /// CharacterReSpecResponse, the answer to both faction and veteran
    /// respecs: result u8 (0x14cfe8), then the character summary when it is
    /// 0 (marshal 0x8c35ac; 2.12 0x70fcb4). The emulator sends 1 on failure.
    /// </summary>
    public static byte[] CharacterReSpecResponse(MagClientVersion version, bool success, MagCharacter? character)
    {
        ZipperBitWriter writer = Start(version.Messages.CharacterReSpecResponse);
        writer.WriteU8(success ? (byte)0 : (byte)1);
        if (success)
            (character ?? new MagCharacter()).WriteSummary(writer, version);
        return writer.ToArray();
    }

    /// <summary>
    /// The answer to the launcher's PatchRequest: the files to fetch over DLS
    /// and the DLS settings. With no files the launcher skips DLS, runs its
    /// post-process step and boots bin/mage_g.self.
    /// <list type="bullet">
    /// <item>1.00: one PatchResponse (0x1F6, demarshal EBOOT 0x8ba7c): u16
    /// count, the files, then <see cref="MapsPatchSettings"/>. The handler
    /// (EBOOT 0x133a4) stores it and moves to state 4 [C].</item>
    /// <item>2.12: PatchSettings (0x1F6, demarshal EBOOT 0x5a50c), which the
    /// handler (EBOOT 0x123e0) stores; then one PatchURL (0x1F7, marshal
    /// EBOOT 0x5a7e4) per file, a file and an isLast bit, the handler adding
    /// each named file to its list. On the last one it hands the list to the
    /// settings (0x191c0) and moves to state 4 like 1.00 [C]. With no files a
    /// single PatchURL with an empty name and isLast set ends the check.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<byte[]> PatchReply(MagClientVersion version, IReadOnlyList<MapsPatchFile> files, MapsPatchSettings? settings = null)
    {
        settings ??= new MapsPatchSettings();
        MagMessageIds ids = version.Messages;
        if (ids.PatchResponse is { } response)
        {
            ZipperBitWriter writer = Start(response);
            writer.WriteU16((ushort)files.Count);
            foreach (MapsPatchFile file in files)
                file.Write(writer);
            settings.Write(writer);
            return [writer.ToArray()];
        }
        if (ids.PatchSettings is not { } settingsType || ids.PatchUrl is not { } urlType)
            throw new InvalidOperationException($"{version} has neither PatchResponse nor PatchSettings/PatchURL");
        List<byte[]> messages = [];
        ZipperBitWriter head = Start(settingsType);
        settings.Write(head);
        messages.Add(head.ToArray());
        IReadOnlyList<MapsPatchFile> urls = files.Count > 0 ? files : [new MapsPatchFile()];
        for (int i = 0; i < urls.Count; i++)
        {
            ZipperBitWriter url = Start(urlType);
            urls[i].Write(url);
            url.WriteBool(i == urls.Count - 1);
            messages.Add(url.ToArray());
        }
        return messages;
    }

    public static byte[] ProtocolInfo(MagClientVersion version, uint protocol, uint build)
    {
        ZipperBitWriter writer = Start(version.Messages.ProtocolInfo);
        writer.WriteU32(protocol);
        writer.WriteU32(build);
        return writer.ToArray();
    }

    /// <summary>
    /// MAPSHello, in the order the client's demarshal reads it (mage_g
    /// 0x155d00; 2.12 0x111e4c, same reads): key, transID, success, online,
    /// a 32-bit field the development server named m_availableFactions, a
    /// string, and a u16 that bounds the ticket chunks the client then sends
    /// (its chunk buffer is 900 bytes).
    /// </summary>
    public static byte[] MapsHello(MagClientVersion version, MapsEnvelope envelope, bool success, bool online, uint factions, string message, ushort ticketChunkSize)
    {
        ZipperBitWriter writer = Start(version.Messages.MapsHello);
        envelope.Write(writer);
        writer.WriteBool(success);
        writer.WriteBool(online);
        writer.WriteU32(factions);
        writer.WriteString(message);
        writer.WriteU16(ticketChunkSize);
        return writer.ToArray();
    }

    /// <summary>NpAuthenticateResponse, AccountLogoutResponse and DeleteCharacterResponse share this shape: envelope and a success bit.</summary>
    public static byte[] StatusResponse(ushort type, MapsEnvelope envelope, bool success)
    {
        ZipperBitWriter writer = Start(type);
        envelope.Write(writer);
        writer.WriteBool(success);
        return writer.ToArray();
    }

    /// <summary>One universe; the client reads them until one has isLast set (mage_g 0x155ad0; 2.12 0x112930).</summary>
    public static byte[] UniverseListResponse(MagClientVersion version, MapsEnvelope envelope, uint id, string name, string authDns, string authIp, uint authPort, string svoUrl, bool success, bool isLast)
    {
        ZipperBitWriter writer = Start(version.Messages.UniverseListResponse);
        envelope.Write(writer);
        writer.WriteU32(id);
        writer.WriteString(name);
        writer.WriteString(authDns);
        writer.WriteString(authIp);
        writer.WriteU32(authPort);
        writer.WriteString(svoUrl);
        writer.WriteBool(success);
        writer.WriteBool(isLast);
        return writer.ToArray();
    }

    /// <summary>A kernel keep-alive: the type alone.</summary>
    public static byte[] KeepAlive(MagClientVersion version)
    {
        ZipperBitWriter writer = Start(version.Messages.KeepAlive);
        return writer.ToArray();
    }

    private static ZipperBitWriter Start(ushort type)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(type);
        return writer;
    }
}

/// <summary>What AccountLoginResponse tells the client about its lobby.</summary>
public sealed record MagLobbyLogin(uint LobbyWorldUid, int AccountId, string ServerIp, ushort ServerPort, uint WorldId,
    string AccessKey, string SessionKey, uint Piuid, byte Result = MagLobbyLogin.Success, ushort ConnectionType = 1, ushort AddressType = 1)
{
    /// <summary>m_result: 1 logs in (handler 0x1c911c reads m_sessionKey and goes on), 2 queues (queue position/time), anything else is an error.</summary>
    public const byte Success = 1;
    public const byte Queued = 2;
    public const byte Failed = 0;
}
