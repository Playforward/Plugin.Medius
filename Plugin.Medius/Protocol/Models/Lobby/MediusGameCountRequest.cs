using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// SCE-RT 3.03 lobby-ext 0x7E. 54 bytes: MessageID (21), SessionKey (17),
    /// four ints, no pad (Killzone 2 1.29: registered at 0x722960, codec
    /// 0x726898 writes bytes(21) bytes(17) u32 x4; sender 0x719ef8) [C; live
    /// 2026-10-31 from both players, 54 bytes]. Killzone 2's game finder (0x4b6e08)
    /// sends one per entry of a list of three-int entries, before asking for
    /// the game list, and waits 60 s for the answer (else error 10002) [C].
    /// Live values 1, 1, -1, 1 [live]. What the fields and the answer mean is
    /// not known; "game count" is a [guess].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.GameCount)]
    public class MediusGameCountRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.GameCount;

        public MessageId MessageID { get; set; }

        public string SessionKey;
        public int Field1;
        public int Field2;
        public int Field3;
        public int Field4;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            Field1 = reader.ReadInt32();
            Field2 = reader.ReadInt32();
            Field3 = reader.ReadInt32();
            Field4 = reader.ReadInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(Field1);
            writer.Write(Field2);
            writer.Write(Field3);
            writer.Write(Field4);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusGameCountResponse() { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusFail };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"SessionKey:{SessionKey} " +
                $"Fields:{Field1},{Field2},{Field3},{Field4}";
        }
    }
}
