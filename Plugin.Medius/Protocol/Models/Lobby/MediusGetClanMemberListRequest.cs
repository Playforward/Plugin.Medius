using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// A clan's members (lobby 0x90; Resistance: Fall of Man patch codec 0x660034): MessageID,
    /// SessionKey, pad 2, ClanID [C]. RFOM asks for its own clan every 10 seconds [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetClanMemberList)]
    public class MediusGetClanMemberListRequest : BaseLobbyMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.GetClanMemberList;

        public MessageId MessageID { get; set; }
        public string SessionKey; // SESSIONKEY_MAXLEN
        public int ClanID;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            ClanID = reader.ReadInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(ClanID);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) => new MediusGetClanMemberListResponse
        {
            MessageID = request.MessageID,
            EndOfList = true,
        };

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} SessionKey:{SessionKey} ClanID:{ClanID}";
    }
}
