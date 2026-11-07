using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// One clan member per message (lobby 0x91; Resistance: Fall of Man patch codec 0x6600b4, struct
    /// 0x44): MessageID, pad 3, AccountID, AccountName[32], a u32 at +0x3c, EndOfList u8, pad 3 [C].
    /// There is no StatusCode. PSRewired sends the member's id and name, 0 and EndOfList [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetClanMemberListResponse)]
    public class MediusGetClanMemberListResponse : BaseLobbyMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.GetClanMemberListResponse;

        public bool IsSuccess => true;

        public MessageId MessageID { get; set; }
        public int AccountID;
        public string AccountName = ""; // ACCOUNTNAME_MAXLEN
        /// <summary>[guess] Unnamed u32 at +0x3c (member status or rank); 0 [live].</summary>
        public uint Unk3C;
        public bool EndOfList;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            AccountID = reader.ReadInt32();
            AccountName = reader.ReadString(Constants.ACCOUNTNAME_MAXLEN);
            Unk3C = reader.ReadUInt32();
            EndOfList = reader.ReadBoolean();
            reader.ReadBytes(3);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(AccountID);
            writer.Write(AccountName, Constants.ACCOUNTNAME_MAXLEN);
            writer.Write(Unk3C);
            writer.Write(EndOfList);
            writer.Write(new byte[3]);
        }

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} AccountID:{AccountID} AccountName:{AccountName} Unk3C:{Unk3C} EndOfList:{EndOfList}";
    }
}
