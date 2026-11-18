using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Answer to <see cref="MediusJoinLeastPopulatedChannelRequest"/> (lobby-ext 0x72, patch codec
    /// 0x657be0): MessageID, StatusCode (+0x18), a u32 (+0x1c), ConnectInfo (+0x20) [C]. Fall of Man
    /// copies the ConnectInfo and connects to it (0x334a08) [C].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.JoinLeastPopulatedChannelResponse)]
    public class MediusJoinLeastPopulatedChannelResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.JoinLeastPopulatedChannelResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }
        public MediusCallbackStatus StatusCode;
        /// <summary>[guess] u32 at +0x1c.</summary>
        public uint Unk1C;
        public NetConnectionInfo ConnectInfo = new();

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            StatusCode = reader.Read<MediusCallbackStatus>();
            Unk1C = reader.ReadUInt32();
            ConnectInfo = reader.Read<NetConnectionInfo>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(StatusCode);
            writer.Write(Unk1C);
            writer.Write(ConnectInfo);
        }

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} StatusCode:{StatusCode} Unk1C:{Unk1C} ConnectInfo:{ConnectInfo}";
    }
}
