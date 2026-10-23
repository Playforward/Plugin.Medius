using Plugin.Medius.Extensions.Zipper;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// MAG's MAPS plugin messages as the retail client (BCUS98110 1.00, build
/// ABmagprod370048) marshals them. Ids come from the message constructors
/// in mage_g (e.g. MAPSHello is built with type 0x36 at 0x1c6c14) and from
/// the client's dispatch table (0x1cb874, types 0..0x39). Development builds
/// numbered the account messages three lower.
/// </summary>
public static class MapsTypes
{
    public const ushort Hello = 0x01;
    public const ushort ProtocolInfo = 0x02;
    /// <summary>
    /// Kernel keep-alive, no body. The client sends one every ~15 s
    /// (NetClientImplScert::UpdateKeepAlive) and drops the connection when
    /// none comes back; its dispatch table handles type 3 from the server.
    /// </summary>
    public const ushort KeepAlive = 0x03;
    /// <summary>
    /// The game logs its character in on the MAS with these (NetAccountLogin*,
    /// vtables 0x1a4dcd0 / 0x1a4dcb0; dev builds numbered them 0x2B/0x2C).
    /// The response tells it where the lobby (MLS) is and which keys to
    /// present there [C, live].
    /// </summary>
    public const ushort AccountLoginRequest = 0x2E;
    public const ushort AccountLoginResponse = 0x2F;
    public const ushort AccountLogoutRequest = 0x30;
    public const ushort AccountLogoutResponse = 0x31;
    public const ushort NpAuthenticateRequest = 0x34;
    public const ushort NpAuthenticateResponse = 0x35;
    public const ushort MapsHello = 0x36;
    public const ushort UniverseListRequest = 0x37;
    public const ushort UniverseListResponse = 0x38;

    /// <summary>
    /// The launcher's patch check (EBOOT.BIN). These are game messages
    /// (NetGameMessage, types 0x1F4 and up), not MAPS client messages: no
    /// key/transID envelope, and the client routes 0x1F6 to the launcher's
    /// OnGameMessage handler (EBOOT 0x133a4).
    /// </summary>
    public const ushort PatchRequest = 0x1F5;
    public const ushort PatchResponse = 0x1F6;

    /// <summary>
    /// The game (not the launcher) asks MAPS for the news and EULA text once
    /// logged in (MagNetClient, "Sending News/EULA request to MAPSe"). The
    /// request is the envelope and a language string ("english"). Ids from
    /// the constructors at mage_g 0x4561f0 / 0x4560f4 [C, live].
    /// </summary>
    /// <summary>
    /// MAG's character (soldier) messages, ids from the constructors at
    /// mage_g 0x455d50.. and RTTI names [C]. CharacterListResponse is only
    /// the envelope and a success bit (marshal 0x4528bc, demarshal 0x452408);
    /// the soldiers themselves come through CharacterData.
    /// </summary>
    public const ushort CharacterListRequest = 0x236;
    public const ushort CharacterListResponse = 0x237;
    public const ushort CharacterDataRequest = 0x238;
    public const ushort CharacterDataResponse = 0x239;
    public const ushort CreateCharacterRequest = 0x23A;
    public const ushort CreateCharacterResponse = 0x23B;
    public const ushort DeleteCharacterRequest = 0x23C;
    public const ushort DeleteCharacterResponse = 0x23D;

    /// <summary>
    /// The lobby polls server status (queues per game mode and PMC, Shadow
    /// War) right after connecting to the MLS; ids from the constructors at
    /// 0x8c7a28 / 0x8c7550 [C, live].
    /// </summary>
    public const ushort ServerStatusRequest = 0x24B;
    public const ushort ServerStatusResponse = 0x24C;

    /// <summary>Loadout edits (game messages, no envelope): index + loadout in, a success bit out [C, live].</summary>
    public const ushort LoadoutUpdateRequest = 0x21F;
    public const ushort LoadoutUpdateResponse = 0x221;

    public const ushort NewsEulaRequest = 0x23E;
    public const ushort NewsEulaResponse = 0x23F;

    /// <summary>
    /// The network protocol the retail client insists on
    /// (NetClientImpl::CheckNetworkProtocol compares against the global at
    /// mage_g 0x15ad1d0). The build number it checks is
    /// <c>strtol("ABmagprod370048")</c>, which is 0.
    /// </summary>
    public const uint RetailProtocol = 1740;
    public const uint RetailBuild = 0;
}

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
/// PatchRequest (0x1F5), as the launcher marshals it (EBOOT 0x8abec): the
/// patch levels it already has on the HDD, its build string and language.
/// Both levels are 0 when it holds fewer than two patch entries.
/// </summary>
public sealed class MapsPatchRequest
{
    public int Highest { get; init; }
    public int Initial { get; init; }
    public string Build { get; init; } = "";
    public string Language { get; init; } = "";

    public static MapsPatchRequest Read(ZipperBitReader reader) => new()
    {
        Highest = reader.ReadI32(),
        Initial = reader.ReadI32(),
        Build = reader.ReadString(),
        Language = reader.ReadString(),
    };
}

/// <summary>One file a PatchResponse tells the launcher to fetch over SCE-RT DLS.</summary>
public sealed class MapsPatchFile
{
    public string Name { get; init; } = "";
    /// <summary>Taken to be unknown/generic/psarc (0/1/2); a psarc reserves three times its size on the HDD.</summary>
    public byte Kind { get; init; }
    public uint Size { get; init; }
}

public static class MapsWriters
{
    /// <summary>
    /// PatchResponse (0x1F6), in the order the launcher demarshals it (EBOOT
    /// 0x8ba7c): the files, then the fields DLSImpl copies into its cellDls
    /// activate parameters (EBOOT 0x67fb4). The defaults are the message
    /// constructor's (0x8aa38). With no files the launcher skips DLS
    /// entirely, runs its post-process step and boots bin/mage_g.self.
    /// </summary>
    /// <summary>
    /// NewsEulaResponse (demarshal mage_g 0x455818): envelope, done:bit,
    /// kind:u8 (0 news, 1 EULA), text:string, u32. The client appends each
    /// response's text to that kind's buffer and stores the u32 with it
    /// (0x4d04cc); it carries on only once both kinds have arrived with done
    /// set (flags +0x24c/+0x24d, checked at 0x89f20).
    /// </summary>
    public static byte[] NewsEulaResponse(MapsEnvelope envelope, byte kind, string text, uint version, bool done)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.NewsEulaResponse);
        envelope.Write(writer);
        writer.WriteBool(done);
        writer.WriteU8(kind);
        writer.WriteString(text);
        writer.WriteU32(version);
        return writer.ToArray();
    }

    /// <summary>
    /// CharacterListResponse as MAG actually sends it: the subclass
    /// MagNetMessageCharacterListResponse (vtable 0x1a79e60, demarshal
    /// 0x8c1414) adds a byte its constructor sets to 1 and a u8 count of
    /// character summaries (read by 0x8bd468) after the envelope and success
    /// bit. The base class alone leaves the client waiting.
    /// </summary>
    public static byte[] CharacterListResponse(MapsEnvelope envelope, bool success, byte flag, IReadOnlyList<byte[]> summaries)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.CharacterListResponse);
        envelope.Write(writer);
        writer.WriteBool(success);
        writer.WriteU8(flag);
        writer.WriteU8((byte)summaries.Count);
        if (summaries.Count > 0)
            throw new NotImplementedException("character summaries are not encoded yet");
        return writer.ToArray();
    }

    /// <summary>
    /// CreateCharacterResponse (demarshal mage_g 0x453a58): envelope and a
    /// 32-bit result, 0 = kNetSuccess (the first of the client's kNet*
    /// results; kNetNameTaken / kNetNameInvalid follow later in the list).
    /// </summary>
    public static byte[] CreateCharacterResponse(MapsEnvelope envelope, int result)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.CreateCharacterResponse);
        envelope.Write(writer);
        writer.WriteI32(result);
        return writer.ToArray();
    }

    /// <summary>
    /// AccountLoginResponse (demarshal mage_g 0x1e169c, field names from its
    /// Dump at 0x1e3914): lobbyWorldUID, accountID, connectionType,
    /// addressType, serverIP char[16], serverPort, worldID, accessKey
    /// char[17], sessionKey char[17], result u8 (1 success, 2 queued), piuid,
    /// queuePosition, queueTimeLeft, timestamp (two u32).
    /// </summary>
    public static byte[] AccountLoginResponse(MapsEnvelope envelope, MagLobbyLogin login)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.AccountLoginResponse);
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

    private static void WriteFixed(ZipperBitWriter writer, string text, int length)
    {
        byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text ?? "");
        for (int i = 0; i < length; i++)
            writer.WriteU8(i < bytes.Length && i < length - 1 ? bytes[i] : (byte)0);
    }

    /// <summary>
    /// CharacterDataResponse as MAG sends it (MagNetMessageCharacterDataResponse,
    /// demarshal 0x8c0e50): the base part (envelope, success bit, a byte at
    /// +0x5d; 0x452634) and the character record.
    /// </summary>
    public static byte[] CharacterDataResponse(MapsEnvelope envelope, bool success, byte index, MagCharacter? character)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.CharacterDataResponse);
        envelope.Write(writer);
        writer.WriteBool(success);
        writer.WriteU8(index);
        (character ?? new MagCharacter()).WriteRecord(writer);
        return writer.ToArray();
    }

    /// <summary>
    /// ServerStatusResponse (demarshal mage_g 0x8c7894): envelope;
    /// gameMinPlayers[4][3], playerInGameCounts[4][3] (u32); shadow war
    /// contracts [3][3] (s8, 0x3abc90); playerQueueSizes[4][3], four more u32
    /// (shadow war scores); queueControls[4][3] (bits, 0x18c220). Four game
    /// modes by three PMCs.
    /// </summary>
    public static byte[] ServerStatusResponse(MapsEnvelope envelope, uint minPlayers, bool queuesOpen)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.ServerStatusResponse);
        envelope.Write(writer);
        for (int i = 0; i < 12; i++) writer.WriteU32(minPlayers);
        for (int i = 0; i < 12; i++) writer.WriteU32(0);
        for (int i = 0; i < 9; i++) writer.WriteU8(0);
        for (int i = 0; i < 12; i++) writer.WriteU32(0);
        for (int i = 0; i < 4; i++) writer.WriteU32(0);
        for (int i = 0; i < 12; i++) writer.WriteBool(queuesOpen);
        return writer.ToArray();
    }

    public static byte[] PatchResponse(IReadOnlyList<MapsPatchFile> files)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.PatchResponse);
        writer.WriteU16((ushort)files.Count);
        foreach (MapsPatchFile file in files)
        {
            writer.WriteString(file.Name);
            writer.WriteU8(file.Kind);
            writer.WriteU32(file.Size);
        }
        for (int i = 0; i < 3; i++)
            writer.WriteI32(-1);
        writer.WriteU32(0x2000);
        writer.WriteU8(0);
        for (int i = 0; i < 6; i++)
            writer.WriteI32(-1);
        return writer.ToArray();
    }

    public static byte[] ProtocolInfo(uint protocol, uint build)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.ProtocolInfo);
        writer.WriteU32(protocol);
        writer.WriteU32(build);
        return writer.ToArray();
    }

    /// <summary>
    /// MAPSHello, in the order the client's demarshal reads it (mage_g
    /// 0x155d00): key, transID, success, online, a 32-bit field the
    /// development server named m_availableFactions, a string, and a u16
    /// that bounds the ticket chunks the client then sends (its chunk buffer
    /// is 900 bytes).
    /// </summary>
    public static byte[] MapsHello(MapsEnvelope envelope, bool success, bool online, uint factions, string message, ushort ticketChunkSize)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.MapsHello);
        envelope.Write(writer);
        writer.WriteBool(success);
        writer.WriteBool(online);
        writer.WriteU32(factions);
        writer.WriteString(message);
        writer.WriteU16(ticketChunkSize);
        return writer.ToArray();
    }

    /// <summary>NpAuthenticateResponse and AccountLogoutResponse share this shape: envelope and a success bit.</summary>
    public static byte[] StatusResponse(ushort type, MapsEnvelope envelope, bool success)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(type);
        envelope.Write(writer);
        writer.WriteBool(success);
        return writer.ToArray();
    }

    /// <summary>One universe; the client reads them until one has isLast set (mage_g 0x155ad0).</summary>
    public static byte[] UniverseListResponse(MapsEnvelope envelope, uint id, string name, string authDns, string authIp, uint authPort, string svoUrl, bool success, bool isLast)
    {
        ZipperBitWriter writer = new();
        writer.WriteU16(MapsTypes.UniverseListResponse);
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
