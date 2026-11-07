using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    // The SCE-RT 3.03 matchmaking ("MMS") messages. Layouts follow the
    // client's own (de)serializers in Resistance 2's EBOOT, which write the
    // fields back to back with no implicit padding: bytes(n), u32 and u8
    // little-endian, and explicit pads only where the client writes them.
    // Addresses below are those serializers [C]; field names the client
    // never names are [guess] or UnkNN placeholders (NN = offset in the
    // client's decoded struct). See docs/MATCHMAKING.md.

    /// <summary>
    /// Asks for the supersets (matchmaking pools) the title offers. Client
    /// serializer 0x8078a8: MessageID, SessionKey.
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MatchListSupersets)]
    public class MediusMatchListSupersetsRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MatchListSupersets;

        public MessageId MessageID { get; set; }
        public string SessionKey;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) => new MediusMatchListSupersetsResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusFail,
            EndOfList = true,
        };

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} SessionKey:{SessionKey}";
    }

    /// <summary>
    /// One superset per message; the client keeps reading until EndOfList.
    /// Client serializer 0x80a320: MessageID, StatusCode (no pad before it),
    /// EndOfList u8, SupersetID, Unk24, three lengths, then the three
    /// strings without terminators (the client adds them).
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MatchListSupersetsResponse)]
    public class MediusMatchListSupersetsResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MatchListSupersetsResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }
        public MediusCallbackStatus StatusCode;
        public bool EndOfList;
        public uint SupersetID;
        /// <summary>[guess] Unnamed u32 at +0x24 of the client's struct.</summary>
        public uint Unk24;
        public string SupersetName = "";
        public string SupersetDescription = "";
        public string SupersetExtraInfo = "";

        /// <summary>
        /// Layout switch, not a field: SCE-RT 3.98/3.02 write the StatusCode again after EndOfList
        /// (Resistance: Fall of Man codecs 0x61561c disc, 0x6580ec patched) [C]. Set from the
        /// title's <see cref="ScertLayout.SupersetStatusCodeRepeated"/>.
        /// </summary>
        public bool StatusCodeRepeated;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            StatusCode = reader.Read<MediusCallbackStatus>();
            EndOfList = reader.ReadByte() != 0;
            if (StatusCodeRepeated)
                StatusCode = reader.Read<MediusCallbackStatus>();
            SupersetID = reader.ReadUInt32();
            Unk24 = reader.ReadUInt32();
            int nameLength = reader.ReadInt32(), descriptionLength = reader.ReadInt32(), extraLength = reader.ReadInt32();
            SupersetName = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            SupersetDescription = Encoding.UTF8.GetString(reader.ReadBytes(descriptionLength));
            SupersetExtraInfo = Encoding.UTF8.GetString(reader.ReadBytes(extraLength));
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            byte[] name = Encoding.UTF8.GetBytes(SupersetName ?? "");
            byte[] description = Encoding.UTF8.GetBytes(SupersetDescription ?? "");
            byte[] extra = Encoding.UTF8.GetBytes(SupersetExtraInfo ?? "");
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(StatusCode);
            writer.Write(EndOfList ? (byte)1 : (byte)0);
            if (StatusCodeRepeated)
                writer.Write(StatusCode);
            writer.Write(SupersetID);
            writer.Write(Unk24);
            writer.Write(name.Length);
            writer.Write(description.Length);
            writer.Write(extra.Length);
            writer.Write(name);
            writer.Write(description);
            writer.Write(extra);
        }

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} StatusCode:{StatusCode} EndOfList:{EndOfList} SupersetID:{SupersetID} Unk24:{Unk24}" +
            $" SupersetName:{SupersetName} SupersetDescription:{SupersetDescription} SupersetExtraInfo:{SupersetExtraInfo}";
    }

    /// <summary>
    /// Queues a party (one or more players) for a match in a superset.
    /// Client serializer 0x808900. The attributes blob is the game's own
    /// (Resistance 2 builds it in lobby_matchMgr 0x25e950; see the title's
    /// docs); the server reads it through the title's match rules.
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MatchParty)]
    public class MediusMatchPartyRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MatchParty;

        public MessageId MessageID { get; set; }
        public string SessionKey;
        /// <summary>[guess] 16 bytes at +0x26; Resistance 2 leaves them zero.</summary>
        public byte[] Unk26 = new byte[16];
        public uint SupersetID;
        /// <summary>[guess] u32 at +0x40; Resistance 2 sends 2.</summary>
        public uint Unk40;
        /// <summary>[guess] u32 at +0x44; Resistance 2 sends 8 for co-op and 60 otherwise (seconds to wait?).</summary>
        public uint Unk44;
        /// <summary>[guess] u32 at +0x48; Resistance 2 sends 0.</summary>
        public uint Unk48;
        /// <summary>[guess] u32 at +0x4c; Resistance 2 sends 3.</summary>
        public uint Unk4C;
        /// <summary>The party's members, one u32 each (count at +0x3c).</summary>
        public uint[] PlayerAccountIDs = [];
        /// <summary>Game-defined match attributes (length at +0x50).</summary>
        public byte[] Attributes = [];

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            Unk26 = reader.ReadBytes(16);
            SupersetID = reader.ReadUInt32();
            int playerCount = reader.ReadInt32();
            Unk40 = reader.ReadUInt32();
            Unk44 = reader.ReadUInt32();
            Unk48 = reader.ReadUInt32();
            Unk4C = reader.ReadUInt32();
            int attributesLength = reader.ReadInt32();
            PlayerAccountIDs = new uint[playerCount];
            for (int i = 0; i < playerCount; i++)
                PlayerAccountIDs[i] = reader.ReadUInt32();
            Attributes = reader.ReadBytes(attributesLength);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(Unk26 ?? new byte[16], 16);
            writer.Write(SupersetID);
            writer.Write((PlayerAccountIDs ?? []).Length);
            writer.Write(Unk40);
            writer.Write(Unk44);
            writer.Write(Unk48);
            writer.Write(Unk4C);
            writer.Write((Attributes ?? []).Length);
            foreach (uint id in PlayerAccountIDs ?? [])
                writer.Write(id);
            writer.Write(Attributes ?? []);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) =>
            new MediusMatchPartyResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusFail };

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} SessionKey:{SessionKey} SupersetID:{SupersetID} Unk40:{Unk40} Unk44:{Unk44} Unk48:{Unk48} Unk4C:{Unk4C}" +
            $" PlayerAccountIDs:[{string.Join(",", PlayerAccountIDs ?? [])}] Attributes:{Convert.ToHexString(Attributes ?? [])}";
    }

    /// <summary>
    /// The plain status answer the client registers for MatchParty (0x89)
    /// and MatchCloseLateJoin (0x7A): client serializer 0x808bf0,
    /// MessageID, 3 pad bytes, StatusCode.
    /// </summary>
    public abstract class MediusMatchStatusResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }
        public MediusCallbackStatus StatusCode;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
        }

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} StatusCode:{StatusCode}";
    }

    /// <summary>The party is queued (or refused); the match itself comes later as <see cref="MediusMatchFoundResponse"/>.</summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MatchPartyResponse)]
    public class MediusMatchPartyResponse : MediusMatchStatusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MatchPartyResponse;
    }

    /// <summary>
    /// Tells the matchmaker a matched game takes no more late joiners.
    /// Client serializer 0x808760: MessageID, SessionKey, then a u32 (no pad
    /// before it) that Resistance 2 leaves zero.
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MatchCloseLateJoin)]
    public class MediusMatchCloseLateJoinRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MatchCloseLateJoin;

        public MessageId MessageID { get; set; }
        public string SessionKey;
        /// <summary>[guess] u32 at +0x28 (a world id?); Resistance 2 sends 0.</summary>
        public uint Unk28;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            Unk28 = reader.ReadUInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(Unk28);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) =>
            new MediusMatchCloseLateJoinResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusFail };

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} SessionKey:{SessionKey} Unk28:{Unk28}";
    }

    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MatchCloseLateJoinResponse)]
    public class MediusMatchCloseLateJoinResponse : MediusMatchStatusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MatchCloseLateJoinResponse;
    }

    /// <summary>
    /// The result of a <see cref="MediusMatchPartyRequest"/>, sent with that
    /// request's MessageID after its <see cref="MediusMatchPartyResponse"/>.
    /// Client serializer 0x809d10: MessageID, 16 bytes, four u32 (the third
    /// is the status), then, only when the status is
    /// <see cref="MediusCallbackStatus.MediusJoinAssignedGame"/>, the size of
    /// the decoded game info and the game info itself. The client treats any
    /// other status as "no match" (0x75a084).
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MatchFoundResponse)]
    public class MediusMatchFoundResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MatchFoundResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }
        /// <summary>[guess] 16 bytes at +0x15.</summary>
        public byte[] Unk15 = new byte[16];
        /// <summary>[guess] u32 at +0x28.</summary>
        public uint Unk28;
        /// <summary>[guess] u32 at +0x2c.</summary>
        public uint Unk2C;
        public MediusCallbackStatus StatusCode;
        /// <summary>[guess] u32 at +0x34; handed to the game's match callback as its third argument.</summary>
        public uint Unk34;
        /// <summary>Present when <see cref="StatusCode"/> is MediusJoinAssignedGame.</summary>
        public MediusMatchGameInfo GameInfo;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            Unk15 = reader.ReadBytes(16);
            Unk28 = reader.ReadUInt32();
            Unk2C = reader.ReadUInt32();
            StatusCode = reader.Read<MediusCallbackStatus>();
            Unk34 = reader.ReadUInt32();
            GameInfo = null;
            if (StatusCode == MediusCallbackStatus.MediusJoinAssignedGame && reader.ReadInt32() > 0)
                GameInfo = reader.Read<MediusMatchGameInfo>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(Unk15 ?? new byte[16], 16);
            writer.Write(Unk28);
            writer.Write(Unk2C);
            writer.Write(StatusCode);
            writer.Write(Unk34);
            if (StatusCode == MediusCallbackStatus.MediusJoinAssignedGame)
            {
                writer.Write(GameInfo?.DecodedSize ?? 0);
                if (GameInfo is not null)
                    writer.Write(GameInfo);
            }
        }

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} Unk28:{Unk28} Unk2C:{Unk2C} StatusCode:{StatusCode} Unk34:{Unk34} GameInfo:{GameInfo}";
    }

    /// <summary>
    /// The matched game (client serializer 0x809ad8): world id, 32 bytes,
    /// a u32, the address list, the game's options blob and the roster.
    /// Sizes on the wire are those of the client's decoded structs
    /// (0x81a3b8: 100 + options + roster), not wire sizes.
    /// </summary>
    public class MediusMatchGameInfo : IStreamSerializer
    {
        public int MediusWorldID;
        /// <summary>
        /// 32 bytes at +0x04: the match's name. PSRewired's R2 server sends
        /// "RANKED-c3187bff61" (NUL-padded) [live R2 1.60]; R2 does not read it [C].
        /// </summary>
        public string MatchName = "";
        /// <summary>[guess] u32 at +0x24.</summary>
        public uint Unk24;
        public NetAddressList AddressList = new();
        /// <summary>Game-defined options for the match (Resistance 2: an "mpoptionsset" record).</summary>
        public byte[] GameOptions = [];
        public List<MediusMatchTeam> Teams = [];

        /// <summary>Size of the client's decoded roster (0x81a330): 8 + 4 per team + each team's.</summary>
        public int RosterDecodedSize => 8 + Teams.Sum(t => 4 + t.DecodedSize);

        /// <summary>Size of the client's decoded game info (0x81a3b8).</summary>
        public int DecodedSize => 100 + (GameOptions?.Length ?? 0) + (Teams.Count > 0 ? RosterDecodedSize : 0);

        public void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            MediusWorldID = reader.ReadInt32();
            MatchName = reader.ReadString(32);
            Unk24 = reader.ReadUInt32();
            AddressList = reader.Read<NetAddressList>();
            GameOptions = reader.ReadBytes(reader.ReadInt32());
            Teams = [];
            if (reader.ReadInt32() <= 0)
                return;
            int teamCount = reader.ReadInt32();
            for (int i = 0; i < teamCount; i++)
            {
                reader.ReadInt32();
                Teams.Add(reader.Read<MediusMatchTeam>());
            }
        }

        public void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(MediusWorldID);
            writer.Write(MatchName ?? "", 32);
            writer.Write(Unk24);
            writer.Write(AddressList ?? new NetAddressList());
            writer.Write((GameOptions ?? []).Length);
            writer.Write(GameOptions ?? []);
            if (Teams.Count == 0)
            {
                writer.Write(0);
                return;
            }
            writer.Write(RosterDecodedSize);
            writer.Write(Teams.Count);
            foreach (MediusMatchTeam team in Teams)
            {
                writer.Write(team.DecodedSize);
                writer.Write(team);
            }
        }

        public override string ToString() =>
            $"World:{MediusWorldID} Name:{MatchName} Unk24:{Unk24} Options:{Convert.ToHexString(GameOptions ?? [])} Teams:[{string.Join(" | ", Teams)}]";
    }

    /// <summary>One team of a matched game (client serializer 0x8087d0).</summary>
    public class MediusMatchTeam : IStreamSerializer
    {
        public List<MediusMatchTeamMember> Members = [];

        /// <summary>Size of the client's decoded team (0x81a310): 8 + 8 per member.</summary>
        public int DecodedSize => 8 + 8 * Members.Count;

        public void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            int count = reader.ReadInt32();
            Members = [];
            for (int i = 0; i < count; i++)
                Members.Add(new MediusMatchTeamMember { AccountID = reader.ReadInt32(), Unk00 = reader.ReadUInt32() });
        }

        public void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(Members.Count);
            foreach (MediusMatchTeamMember member in Members)
            {
                writer.Write(member.AccountID);
                writer.Write(member.Unk00);
            }
        }

        public override string ToString() => string.Join(",", Members);
    }

    /// <summary>
    /// A roster entry. On the wire the account id comes first; the client
    /// stores it at +4 and the other u32 at +0, of which Resistance 2 keeps
    /// only the low byte (MatchFoundCB 0x25e660).
    /// </summary>
    public class MediusMatchTeamMember
    {
        public int AccountID;
        /// <summary>[guess] u32 stored at +0 of the client's entry.</summary>
        public uint Unk00;

        public override string ToString() => $"{AccountID}/{Unk00}";
    }
}
