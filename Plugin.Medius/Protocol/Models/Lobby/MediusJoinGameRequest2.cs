using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Joins a game world (SCE-RT 3.03). 196 bytes: MessageID (21), SessionKey
    /// (17), 2 pad, MediusWorldID, GamePassword (32), GameHostType, pubKey (64),
    /// AddressList (48), Unknown_C0 (Killzone 2 1.29: lobby-ext 0x87 registered
    /// at 0x720be8, codec 0x727560; sender 0x71af80) [C]. Unlike
    /// MediusJoinGameRequest there is no JoinType. Killzone 2's builder
    /// (0x4b9388) sets the world found by name, GameHostType 4 and the key, and
    /// leaves the address list and Unknown_C0 zero [C].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.JoinGame2)]
    public class MediusJoinGameRequest2 : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.JoinGame2;

        public MessageId MessageID { get; set; }

        public string SessionKey;
        public int MediusWorldID;
        public string GamePassword;
        public MediusGameHostType GameHostType;
        public RSA_KEY pubKey;
        public NetAddressList AddressList;
        public int Unknown_C0;

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
            Unknown_C0 = reader.ReadInt32();
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
            writer.Write(pubKey ?? new RSA_KEY());
            writer.Write(AddressList ?? new NetAddressList());
            writer.Write(Unknown_C0);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusJoinGameResponse2() { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusFail, ConnectInfo = new NetConnectionInfo() };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"SessionKey:{SessionKey} " +
                $"MediusWorldID:{MediusWorldID} " +
                $"GamePassword:{GamePassword} " +
                $"GameHostType:{GameHostType} " +
                $"pubKey:{pubKey} " +
                $"AddressList:{AddressList} " +
                $"Unknown_C0:{Unknown_C0}";
        }
    }
}
