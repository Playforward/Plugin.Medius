using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// One buddy per message (lobby 0x3E; Resistance: Fall of Man patch codec 0x65f918, struct 0x48):
    /// MessageID, pad 3, StatusCode, AccountID, AccountName[32], PlayerStatus, EndOfList u8, pad 3 [C].
    /// An empty list is StatusCode 1 (NoResult) with EndOfList [live, PSRewired].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetBuddyListResponse)]
    public class MediusGetBuddyListResponse : BaseLobbyMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.GetBuddyListResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }
        public MediusCallbackStatus StatusCode;
        public int AccountID;
        public string AccountName = ""; // ACCOUNTNAME_MAXLEN
        public MediusPlayerStatus PlayerStatus;
        public bool EndOfList;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            AccountID = reader.ReadInt32();
            AccountName = reader.ReadString(Constants.ACCOUNTNAME_MAXLEN);
            PlayerStatus = reader.Read<MediusPlayerStatus>();
            EndOfList = reader.ReadBoolean();
            reader.ReadBytes(3);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(AccountID);
            writer.Write(AccountName, Constants.ACCOUNTNAME_MAXLEN);
            writer.Write(PlayerStatus);
            writer.Write(EndOfList);
            writer.Write(new byte[3]);
        }

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} StatusCode:{StatusCode} AccountID:{AccountID} AccountName:{AccountName} PlayerStatus:{PlayerStatus} EndOfList:{EndOfList}";
    }
}
