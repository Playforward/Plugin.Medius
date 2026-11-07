using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// The plain buddy list (lobby 0x3D; Resistance: Fall of Man patch codec 0x65f8bc): MessageID,
    /// SessionKey [C]. Sent once on joining the MLS [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetBuddyList)]
    public class MediusGetBuddyListRequest : BaseLobbyMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.GetBuddyList;

        public MessageId MessageID { get; set; }
        public string SessionKey; // SESSIONKEY_MAXLEN

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

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) => new MediusGetBuddyListResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusFail,
            EndOfList = true,
        };

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} SessionKey:{SessionKey}";
    }
}
