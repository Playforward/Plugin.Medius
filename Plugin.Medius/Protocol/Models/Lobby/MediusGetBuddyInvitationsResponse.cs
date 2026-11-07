using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// One waiting buddy invitation. 72 bytes: MessageID (21), 3 pad,
    /// StatusCode, AccountID, AccountName (32) of the inviter, AddType,
    /// EndOfList, 3 pad (Killzone 2 1.29: lobby-ext 0x09, codec 0x733460,
    /// handler 0x72b708) [C].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.GetBuddyInvitationsResponse)]
    public class MediusGetBuddyInvitationsResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.GetBuddyInvitationsResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public int AccountID;
        public string AccountName; // ACCOUNTNAME_MAXLEN
        public MediusBuddyAddType AddType;
        public bool EndOfList;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            AccountID = reader.ReadInt32();
            AccountName = reader.ReadString(Constants.ACCOUNTNAME_MAXLEN);
            AddType = reader.Read<MediusBuddyAddType>();
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
            writer.Write(AddType);
            writer.Write(EndOfList);
            writer.Write(new byte[3]);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"StatusCode:{StatusCode} " +
                $"AccountID:{AccountID} " +
                $"AccountName:{AccountName} " +
                $"AddType:{AddType} " +
                $"EndOfList:{EndOfList}";
        }
    }
}
