using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Resistance: Fall of Man's quick match (lobby-ext 0x78, patch codec 0x65697c): MessageID,
    /// SessionKey, SupersetID (+0x28), PlayerCount (+0x2c), three u32, ExtraLength (+0x3c), then
    /// PlayerCount u32 account ids (+0x40) and ExtraLength bytes (+0x44) [C]. RFOM sends one player
    /// (itself), no extra data, superset 1, 3 or 5 [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MatchFindGame)]
    public class MediusMatchFindGameRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MatchFindGame;

        public MessageId MessageID { get; set; }
        public string SessionKey; // SESSIONKEY_MAXLEN
        public uint SupersetID;
        /// <summary>[guess] Unnamed u32 at +0x30/+0x34/+0x38; 0 [live].</summary>
        public uint Unk30, Unk34, Unk38;
        public List<int> AccountIDs = [];
        public byte[] Extra = [];

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            SupersetID = reader.ReadUInt32();
            int players = reader.ReadInt32();
            Unk30 = reader.ReadUInt32();
            Unk34 = reader.ReadUInt32();
            Unk38 = reader.ReadUInt32();
            int extra = reader.ReadInt32();
            AccountIDs = [];
            for (int i = 0; i < players; i++)
                AccountIDs.Add(reader.ReadInt32());
            Extra = reader.ReadBytes(extra);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(SupersetID);
            writer.Write(AccountIDs.Count);
            writer.Write(Unk30);
            writer.Write(Unk34);
            writer.Write(Unk38);
            writer.Write(Extra.Length);
            foreach (int id in AccountIDs)
                writer.Write(id);
            writer.Write(Extra);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) => new MediusMatchFindGameResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusFail,
        };

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} SessionKey:{SessionKey} SupersetID:{SupersetID} AccountIDs:{string.Join(",", AccountIDs)} Unk30:{Unk30} Unk34:{Unk34} Unk38:{Unk38} Extra:{Extra.Length}B";
    }
}
