using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Answer to <see cref="MediusJoinGameRequest2"/>. 192 bytes: the 3.03
    /// MediusJoinGameResponse (MessageID, 3 pad, StatusCode, GameHostType,
    /// ConnectInfo; 0xBC) then Unknown_BC (Killzone 2 1.29: lobby-ext 0x8E,
    /// size 0x728fd0 returns 0xc0, codec 0x727318) [C]. Killzone 2 copies only
    /// ConnectInfo and connects to the DME with it (0x4b9c98, 0x4b9158) [C].
    /// PS Rewired sends Unknown_BC = 2 and a zero ServerKey [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.JoinGame2Response)]
    public class MediusJoinGameResponse2 : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.JoinGame2Response;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public MediusGameHostType GameHostType;
        public NetConnectionInfo ConnectInfo;
        public int Unknown_BC;

        /// <summary>
        /// Layout switch, not a field: bytes between ConnectInfo and <see cref="Unknown_BC"/>
        /// (0; SCE-RT 3.06 has a u32 there, Killzone 3 codec 0xa29294) [C]. Set from the title's
        /// <see cref="ScertLayout.JoinGameTrailingPadLength"/>.
        /// </summary>
        public int ConnectInfoPadLength;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            GameHostType = reader.Read<MediusGameHostType>();
            ConnectInfo = reader.Read<NetConnectionInfo>();
            reader.ReadBytes(ConnectInfoPadLength);
            Unknown_BC = reader.ReadInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(GameHostType);
            writer.Write(ConnectInfo ?? new NetConnectionInfo());
            writer.Write(new byte[ConnectInfoPadLength]);
            writer.Write(Unknown_BC);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"StatusCode:{StatusCode} " +
                $"GameHostType:{GameHostType} " +
                $"ConnectInfo:{ConnectInfo} " +
                $"Unknown_BC:{Unknown_BC}";
        }
    }
}
