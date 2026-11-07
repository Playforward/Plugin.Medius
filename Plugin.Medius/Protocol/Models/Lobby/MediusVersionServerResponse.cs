using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// The lobby's version string. 77 bytes: MessageID (21) then VersionServer
    /// (56); there is no status code (Killzone 2 1.29: lobby 0x87 registered at
    /// 0x72186c, size 0x72a528 returns 77, codec 0x72d4b8 copies 21 then 56
    /// bytes) [C].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.VersionServerResponse)]
    public class MediusVersionServerResponse : BaseLobbyMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.VersionServerResponse;

        public bool IsSuccess => true;

        public MessageId MessageID { get; set; }

        public string VersionServer;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            VersionServer = reader.ReadString(Constants.VERSIONSERVER_MAXLEN);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(VersionServer, Constants.VERSIONSERVER_MAXLEN);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"VersionServer:{VersionServer}";
        }
    }
}
