using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.JoinGameResponse)]
    public class MediusJoinGameResponse : BaseLobbyMessage, IMediusResponse
    {
		public override byte PacketType => (byte)MediusLobbyMessageIds.JoinGameResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public MediusGameHostType GameHostType;
        public NetConnectionInfo ConnectInfo;

        /// <summary>Bytes after ConnectInfo; null = 4 from Medius 112 on (Horizon), else 0.</summary>
        public int? TrailingPadLength;

        private int TrailingPad(int mediusVersion) => TrailingPadLength ?? (mediusVersion >= 112 ? 4 : 0);

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            //
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            GameHostType = reader.Read<MediusGameHostType>();
            ConnectInfo = reader.Read<NetConnectionInfo>();

            reader.ReadBytes(TrailingPad(reader.MediusVersion));
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(GameHostType);
            writer.Write(ConnectInfo);

            writer.Write(new byte[TrailingPad(writer.MediusVersion)]);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
             $"StatusCode:{StatusCode} " +
$"GameHostType:{GameHostType} " +
$"ConnectInfo:{ConnectInfo}";
        }
    }
}
