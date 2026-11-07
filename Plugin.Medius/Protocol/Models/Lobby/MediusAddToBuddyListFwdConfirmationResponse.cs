using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// A player's answer to a buddy invitation: StatusCode MediusSuccess
    /// accepts, anything else declines. 56 bytes: MessageID (21), 3 pad,
    /// StatusCode, SessionKey (17), 3 pad, OriginatorAccountID, AddType
    /// (Killzone 2 1.29: lobby-ext 0x07, codec 0x731d98; sent from 0x71af50
    /// without a callback, so nothing answers it) [C].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.AddToBuddyListFwdConfirmationResponse)]
    public class MediusAddToBuddyListFwdConfirmationResponse : BaseLobbyExtMessage
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.AddToBuddyListFwdConfirmationResponse;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public string SessionKey; // SESSIONKEY_MAXLEN
        public int OriginatorAccountID;
        public MediusBuddyAddType AddType;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(3);
            OriginatorAccountID = reader.ReadInt32();
            AddType = reader.Read<MediusBuddyAddType>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[3]);
            writer.Write(OriginatorAccountID);
            writer.Write(AddType);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"StatusCode:{StatusCode} " +
                $"SessionKey:{SessionKey} " +
                $"OriginatorAccountID:{OriginatorAccountID} " +
                $"AddType:{AddType}";
        }
    }
}
