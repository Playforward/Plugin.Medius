using Plugin.Medius.RT.Common;

namespace Plugin.Medius;

/// <summary>
/// Message layouts that follow the SCE-RT client library a game links, not
/// the game itself. Two games on the same library lay these messages out the
/// same way, so a title names its library (<see cref="MediusTitle.RtVersion"/>)
/// instead of setting each layout by hand. docs/RT-VERSIONS.md lists the
/// versions seen and the evidence.
/// </summary>
public sealed record ScertLayout
{
    /// <summary>Bytes of MediusUniverseVariableInformationResponse.ExtendedInfo.</summary>
    public int UniverseExtendedInfoLength { get; init; }

    /// <summary>Whether three pad bytes follow the universe information response's EndOfList.</summary>
    public bool UniversePadAfterEndOfList { get; init; }

    /// <summary>
    /// Bytes after MediusJoinGameResponse.ConnectInfo; null keeps Horizon's
    /// rule (4 from Medius 112 on, else 0).
    /// </summary>
    public int? JoinGameTrailingPadLength { get; init; }

    /// <summary>Bytes of DMEServerVersion's version string on the wire.</summary>
    public int DmeServerVersionLength { get; init; }

    /// <summary>
    /// Fixed size of MediusUniverseVariableSvoURLResponse's length-prefixed
    /// URL field (length byte included); null writes only what the URL needs.
    /// </summary>
    public int? SvoUrlFieldLength { get; init; }

    /// <summary>Whether MediusBinaryFwdMessage1 carries OriginatorAccountID before MessageType.</summary>
    public bool BinaryFwdOriginatorFirst { get; init; }

    /// <summary>
    /// Whether MediusMatchListSupersetsResponse writes its StatusCode a second time after
    /// EndOfList (SCE-RT 3.98 and 3.02 serializers pass the same field twice).
    /// </summary>
    public bool SupersetStatusCodeRepeated { get; init; }

    /// <summary>
    /// Whether MediusMatchListSupersetsResponse carries its strings in fixed fields (name 50,
    /// description 256, extra info 256, right after SupersetID) instead of an unnamed u32 and
    /// three lengths before the text (SCE-RT 3.06).
    /// </summary>
    public bool SupersetFixedStrings { get; init; }

    /// <summary>Bytes of MediusGetAnnouncementsResponse.Announcement.</summary>
    public int AnnouncementLength { get; init; } = Constants.ANNOUNCEMENT_MAXLEN;

    /// <summary>
    /// Whether MediusGetClanMemberList_ExtraInfoResponse has LadderStat, LadderPosition and
    /// TotalRankings between OnlineState and EndOfList (SCE-RT 3.06 has none of them).
    /// </summary>
    public bool ClanExtraInfoLadderFields { get; init; } = true;

    /// <summary>
    /// Whether MatchParty, MatchFoundResponse and its game info use the SCE-RT 3.06 layouts
    /// (MatchParty with more fields, MatchFoundResponse without MessageID, a game-world
    /// game info without a roster).
    /// </summary>
    public bool MatchMessages306 { get; init; }
}

/// <summary>A named set of <see cref="ScertLayout"/> defaults for one SCE-RT library line.</summary>
public sealed class ScertLibraryProfile
{
    /// <summary>The library line, "major.minor" (e.g. <c>3.03</c>); empty for <see cref="Default"/>.</summary>
    public string Version { get; }

    public string Name { get; }

    public ScertLayout Layout { get; }

    private ScertLibraryProfile(string version, string name, ScertLayout layout)
    {
        Version = version;
        Name = name;
        Layout = layout;
    }

    /// <summary>
    /// Horizon's layouts (PS2-era Medius), used by titles that name no
    /// library. MAG (SCE-RT 3.04.50/3.04.51) is served with these; no 3.04
    /// client code has been checked against them yet.
    /// </summary>
    public static ScertLibraryProfile Default { get; } = new("", "Horizon (PS2-era)", new ScertLayout
    {
        UniverseExtendedInfoLength = Constants.UNIVERSE_EXTENDED_INFO_MAXLEN,
        UniversePadAfterEndOfList = true,
        JoinGameTrailingPadLength = null,
        DmeServerVersionLength = Constants.DME_VERSION_LENGTH,
        SvoUrlFieldLength = null,
        BinaryFwdOriginatorFirst = false,
    });

    /// <summary>
    /// SCE-RT 3.03 (PS3, 2008). Evidence is from Resistance 2
    /// (3.03.01.200809171107); Killzone 2 (3.03.01.200811271300) shares the
    /// DME reader, its other two layouts are unchecked [guess].
    /// </summary>
    public static ScertLibraryProfile Rt303 { get; } = new("3.03", "SCE-RT 3.03 (PS3)", new ScertLayout
    {
        // Serializer 0x817190 (R2 1.00) / 0x832f28 (R2 1.60): 256-byte ExtendedInfo,
        // nothing after EndOfList [C; live R2 1.60].
        UniverseExtendedInfoLength = 256,
        UniversePadAfterEndOfList = false,
        // The struct is 0xBC bytes, ending with ConnectInfo's 2 pad bytes (R2 1.00
        // swap 0x816580, handler 0x80b1c0 returns 0xbc); 4 more drop the MLS
        // connection, "(00150019)" [C; live R2 1.60].
        JoinGameTrailingPadLength = 0,
        // Reader 0x851798 (R2 1.60; the same reader is at 0x6f6478 in Killzone 2),
        // handler 0x8529b0 copies 0x14 bytes; 16 underruns it, "(00000019)" [C; live R2 1.60].
        DmeServerVersionLength = 20,
        // The client reads the URL into a 0x95-byte struct after the 0x15-byte
        // MessageID (R2 1.00 0x80eb50); PSRewired's R2 server pads the field to
        // 128 bytes (a 151-byte message). Our unpadded 86 bytes also worked [C; live R2 1.60].
        SvoUrlFieldLength = 128,
        // Swap 0x80a7f0 (R2 1.00) reads MessageID then u32, u32, length; PSRewired's
        // R2 server puts the originating account first, then the type (1) in both
        // the game recorder's reply and a player-to-player message [live R2 1.60].
        BinaryFwdOriginatorFirst = true,
    });

    /// <summary>
    /// SCE-RT 3.98 (PS3 launch, 2006; "Medius Client Library Version 3.01.0001").
    /// Evidence is from Resistance: Fall of Man (disc 1.00, 3.98.200610022359):
    /// Horizon's universe and DME sizes, but 3.03's join-game struct and the
    /// rest of the lobby layouts.
    /// </summary>
    public static ScertLibraryProfile Rt398 { get; } = new("3.98", "SCE-RT 3.98 (PS3 2006)", new ScertLayout
    {
        // Serializer 0x621d64 (RFOM 1.00, the field-for-field twin of R2's 0x817190):
        // `li r5,0x80` for ExtendedInfo at +0x2bc, then EndOfList at +0x3bc and
        // nothing after it [C].
        UniverseExtendedInfoLength = 128,
        UniversePadAfterEndOfList = false,
        // Lobby 0x24 is registered at 0x60e144 with handler 0x6166ac returning 0xbc,
        // as R2's [C].
        JoinGameTrailingPadLength = 0,
        // Class 0 type 0 (registered at 0x656650): reader 0x65558c reads 0x10 bytes,
        // handler 0x655fc0 copies 0x10 to conn+0x41c [C].
        DmeServerVersionLength = 16,
        // SvoURL handler 0x619094 reads into the same 0x95-byte struct as R2's
        // 0x80eb50 [C]; the 128-byte field is carried over from 3.03 [guess].
        SvoUrlFieldLength = 128,
        // BinaryFwdMessage1 codec 0x6153e4 is R2's 0x80a7f0 (MessageID, u32, u32,
        // length, data) [C]; the originator-first order is PSRewired's R2 [guess].
        BinaryFwdOriginatorFirst = true,
        // MatchListSupersetsResponse codec 0x61561c writes the StatusCode (+0x18) before and
        // after EndOfList [C].
        SupersetStatusCodeRepeated = true,
    });

    /// <summary>
    /// SCE-RT 3.02 (PS3, 2007; "Medius Client Library Version 3.02.0000"). Evidence is from
    /// Resistance: Fall of Man's in-game patch (3.02.200705211920, RE-Work/rfom/patched): the
    /// same layouts as <see cref="Rt398"/>, which the disc of the same game links.
    /// </summary>
    public static ScertLibraryProfile Rt302 { get; } = new("3.02", "SCE-RT 3.02 (PS3 2007)", new ScertLayout
    {
        // Serializer 0x664984: ExtendedInfo `li r5,0x80` at +0x2bc, EndOfList u8 at +0x3bc,
        // nothing after it [C]; PSRewired's 821-byte answer matches [live].
        UniverseExtendedInfoLength = 128,
        UniversePadAfterEndOfList = false,
        // Lobby 0x24 handler 0x6594e8 and lobby-ext 0x68 handler 0x659324 return 0xbc [C].
        JoinGameTrailingPadLength = 0,
        // Class 0 type 0 (registered at 0x6a977c): reader 0x6a8688 reads 0x10 bytes, handler
        // 0x6a90bc copies 0x10 to conn+0x41c [C].
        DmeServerVersionLength = 16,
        // SvoURL handler 0x65bf38 reads into a 0x95-byte struct [C]; PSRewired pads the URL
        // field to 128 (a 151-byte message) [live].
        SvoUrlFieldLength = 128,
        // BinaryFwdMessage1 codec 0x657e60: MessageID, u32, u32, length, data, as R2's [C].
        BinaryFwdOriginatorFirst = true,
        // MatchListSupersetsResponse codec 0x6580ec writes +0x18 twice, around EndOfList [C];
        // PSRewired's answers carry the second copy (0) [live].
        SupersetStatusCodeRepeated = true,
    });

    /// <summary>
    /// SCE-RT 3.06 (PS3, 2011). Evidence is from Killzone 3 1.14 (3.06.201103140900), static
    /// only: its registration table (register function 0x9ffe14) against Killzone 2 1.29's
    /// (0x700868); see RE-Work/kz3/NOTES.md. 3.03's layouts except where noted.
    /// </summary>
    public static ScertLibraryProfile Rt306 { get; } = new("3.06", "SCE-RT 3.06 (PS3 2011)", new ScertLayout
    {
        // Universe codec 0xa361fc: `li r5,0x100` for ExtendedInfo at +0x2bc, EndOfList u8 at
        // +0x43c, nothing after it, as 3.03 [C].
        UniverseExtendedInfoLength = 256,
        UniversePadAfterEndOfList = false,
        // Lobby 0x24 codec 0xa290d0 writes a u32 at +0xbc after ConnectInfo; the handler's
        // struct grew from 3.03's 0xbc to 0xc0 [C]. Lobby-ext 0x8e (JoinGame2Response, codec
        // 0xa29294) gains the same u32 before its last one [C].
        JoinGameTrailingPadLength = 4,
        // Reader 0xa03130 reads 0x14 bytes (`li r5,0x14`, the twin of Killzone 2's 0x6f6478) [C].
        DmeServerVersionLength = 20,
        // SvoURL handler 0xa2e394 reads into a 0x95-byte struct, as 3.03's 0x72ca58 [C]; the
        // 128-byte field is 3.03's [guess].
        SvoUrlFieldLength = 128,
        // BinaryFwdMessage1 codec 0xa2a0b0: MessageID, u32, u32, u32, data, as 3.03 [C]; the
        // originator-first order is PSRewired's R2 [guess].
        BinaryFwdOriginatorFirst = true,
        // Lobby-ext 0x5e codec 0xa29008: MessageID, StatusCode, EndOfList u8, u32 +0x20, then
        // fixed strings of 0x32 (+0x24), 0x100 (+0x56) and 0x100 (+0x156); struct 0x40 -> 0x258 [C].
        SupersetFixedStrings = true,
        // Lobby 0x4d codec 0xa34950: `li r5,0xfa0` for the text at +0x20 (3.03: 0x3e8) [C].
        AnnouncementLength = 4000,
        // Lobby 0xdb codec 0xa34424: OnlineState is followed by EndOfList and 3 pad bytes;
        // 3.03's three u32 at +0x1cc..+0x1d4 are gone (struct 0x1dc -> 0x1d0) [C].
        ClanExtraInfoLadderFields = false,
        // Lobby-ext 0x88 codec 0xa288e4 (MatchParty), 0x8a codec 0xa29d78 (MatchFoundResponse,
        // no MessageID) and game info 0xa295f8 (a game world, no roster) [C]; 3.03's MatchParty
        // read of a 3.06 body gave superset 0 and no players [live 2026-11-08].
        MatchMessages306 = true,
    });

    /// <summary>Every profile a title can name with <see cref="MediusTitle.RtVersion"/>.</summary>
    public static IReadOnlyList<ScertLibraryProfile> Known { get; } = [Rt303, Rt398, Rt302, Rt306];

    /// <summary>
    /// The profile for an SCE-RT ReleaseVersion string, matched on its
    /// "major.minor" line: <c>3.03</c> and <c>3.03.01.200809171107</c> both
    /// find <see cref="Rt303"/>. Empty finds <see cref="Default"/>; a version no
    /// profile covers finds nothing.
    /// </summary>
    public static bool TryFind(string? rtVersion, out ScertLibraryProfile profile)
    {
        string version = rtVersion?.Trim() ?? "";
        if (version.Length == 0)
        {
            profile = Default;
            return true;
        }

        string[] parts = version.Split('.');
        string line = parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : version;
        profile = Known.FirstOrDefault(p => p.Version == line) ?? Default;
        return profile != Default;
    }

    public override string ToString() => Name;
}
