using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Resistance: Fall of Man's party join (lobby-ext 0x67, patch codec 0x657460, 192 bytes):
    /// MessageID, SessionKey, pad 2, MediusWorldID, GamePassword[32], GameHostType, the client's RSA
    /// public key and its two addresses [C]; the lobby's JoinGame without JoinType. Sent right after
    /// CreateGame2 for the new world, GameHostType 4, addresses zero [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.JoinGameEx)]
    public class MediusJoinGameExRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.JoinGameEx;

        public MessageId MessageID { get; set; }
        public string SessionKey; // SESSIONKEY_MAXLEN
        public int MediusWorldID;
        public string GamePassword = ""; // GAMEPASSWORD_MAXLEN
        public MediusGameHostType GameHostType;
        public RSA_KEY pubKey = new();
        public NetAddressList AddressList = new();

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            MediusWorldID = reader.ReadInt32();
            GamePassword = reader.ReadString(Constants.GAMEPASSWORD_MAXLEN);
            GameHostType = reader.Read<MediusGameHostType>();
            pubKey = reader.Read<RSA_KEY>();
            AddressList = reader.Read<NetAddressList>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(MediusWorldID);
            writer.Write(GamePassword, Constants.GAMEPASSWORD_MAXLEN);
            writer.Write(GameHostType);
            writer.Write(pubKey);
            writer.Write(AddressList);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) => new MediusJoinGameExResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusFail,
            ConnectInfo = new NetConnectionInfo(),
        };

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} SessionKey:{SessionKey} MediusWorldID:{MediusWorldID} GameHostType:{GameHostType} AddressList:{AddressList}";
    }
}
