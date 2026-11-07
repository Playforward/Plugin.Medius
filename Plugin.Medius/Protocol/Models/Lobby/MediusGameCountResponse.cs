using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Answer to <see cref="MediusGameCountRequest"/>. 29 bytes: MessageID (21),
    /// StatusCode, Count, no pad (Killzone 2 1.29: lobby-ext 0x7F, codec 0x726828
    /// reads bytes(21) u32 u32; 0x20 from the size function 0x72bc28 is the
    /// in-memory struct) [C]. On success the client stores the
    /// request's entry with Count beside it (0x4b8198) [C]; PS Rewired answered
    /// 1 with four games listed [live]. The name of Count is a [guess].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.GameCountResponse)]
    public class MediusGameCountResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.GameCountResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public int Count;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            StatusCode = reader.Read<MediusCallbackStatus>();
            Count = reader.ReadInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(StatusCode);
            writer.Write(Count);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"StatusCode:{StatusCode} " +
                $"Count:{Count}";
        }
    }
}
