using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Creates a game world (SCE-RT 3.03). 184 bytes: MessageID (21),
    /// SessionKey (17), 2 pad, ApplicationID, MinPlayers, MaxPlayers, GameName
    /// (64), GamePassword (32), eight generic fields, GameHostType (Killzone 2
    /// 1.29: lobby-ext 0x65 registered at 0x720bb0, codec 0x726bf8; sender
    /// 0x71acd0) [C]. Killzone 2's host sends it after joining its listed game,
    /// for its play world "&lt;game name&gt;P"; joining players send it too, and a
    /// MediusGameNameExists answer is taken as success (0x4b9b18) [C; live].
    /// Its builder (0x4b96c8) fills only ApplicationID, MinPlayers, MaxPlayers,
    /// the name and GameHostType (4) [C]; the generic-field names follow
    /// MediusCreateGameRequest [guess].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.CreateGame2)]
    public class MediusCreateGameRequest2 : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.CreateGame2;

        public MessageId MessageID { get; set; }

        public string SessionKey;
        public int ApplicationID;
        public int MinPlayers;
        public int MaxPlayers;
        public string GameName;
        public string GamePassword;
        public int[] GenericFields = new int[8];
        public MediusGameHostType GameHostType;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            ApplicationID = reader.ReadInt32();
            MinPlayers = reader.ReadInt32();
            MaxPlayers = reader.ReadInt32();
            GameName = reader.ReadString(Constants.GAMENAME_MAXLEN);
            GamePassword = reader.ReadString(Constants.GAMEPASSWORD_MAXLEN);
            for (int i = 0; i < GenericFields.Length; i++)
                GenericFields[i] = reader.ReadInt32();
            GameHostType = reader.Read<MediusGameHostType>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(ApplicationID);
            writer.Write(MinPlayers);
            writer.Write(MaxPlayers);
            writer.Write(GameName, Constants.GAMENAME_MAXLEN);
            writer.Write(GamePassword, Constants.GAMEPASSWORD_MAXLEN);
            for (int i = 0; i < 8; i++)
                writer.Write(i < GenericFields.Length ? GenericFields[i] : 0);
            writer.Write(GameHostType);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusCreateGameResponse2() { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusFail, MediusWorldID = -1 };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"SessionKey:{SessionKey} " +
                $"ApplicationID:{ApplicationID} " +
                $"MinPlayers:{MinPlayers} " +
                $"MaxPlayers:{MaxPlayers} " +
                $"GameName:{GameName} " +
                $"GamePassword:{GamePassword} " +
                $"GenericFields:{string.Join(",", GenericFields)} " +
                $"GameHostType:{GameHostType}";
        }
    }
}
