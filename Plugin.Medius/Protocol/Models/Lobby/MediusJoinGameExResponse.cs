using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Answer to <see cref="MediusJoinGameExRequest"/> (lobby-ext 0x68, patch codec 0x65756c, struct
    /// 0xbc): the JoinGameResponse body (MessageID, pad 3, StatusCode, GameHostType, ConnectInfo) with
    /// nothing after it [C]. PSRewired: host type 4, ClientServerTCPAuxUDP to its game host on TCP/UDP
    /// 11001, the NAT service second, a zero ServerKey [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.JoinGameExResponse)]
    public class MediusJoinGameExResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.JoinGameExResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }
        public MediusCallbackStatus StatusCode;
        public MediusGameHostType GameHostType;
        public NetConnectionInfo ConnectInfo = new();

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            GameHostType = reader.Read<MediusGameHostType>();
            ConnectInfo = reader.Read<NetConnectionInfo>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(GameHostType);
            writer.Write(ConnectInfo);
        }

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} StatusCode:{StatusCode} GameHostType:{GameHostType} ConnectInfo:{ConnectInfo}";
    }
}
