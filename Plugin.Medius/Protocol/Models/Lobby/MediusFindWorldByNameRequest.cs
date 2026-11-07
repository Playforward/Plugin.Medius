using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Looks worlds up by name. 108 bytes: MessageID (21), SessionKey (17),
    /// Name (64), 2 pad, WorldType (Killzone 2 1.29: lobby 0x37 registered at
    /// 0x720fc0, codec 0x730e18; sender 0x719600) [C]. Killzone 2 looks up its
    /// play world "&lt;game name&gt;P" with WorldType 2 (0x4b9570) [C; live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.FindWorldByName)]
    public class MediusFindWorldByNameRequest : BaseLobbyMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.FindWorldByName;

        public MessageId MessageID { get; set; }

        public string SessionKey;
        public string Name;
        public MediusFindWorldType WorldType;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            Name = reader.ReadString(Constants.WORLDNAME_MAXLEN);
            reader.ReadBytes(2);
            WorldType = reader.Read<MediusFindWorldType>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(Name, Constants.WORLDNAME_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(WorldType);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusFindWorldByNameResponse() { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusFail, EndOfList = true };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"SessionKey:{SessionKey} " +
                $"Name:{Name} " +
                $"WorldType:{WorldType}";
        }
    }
}
