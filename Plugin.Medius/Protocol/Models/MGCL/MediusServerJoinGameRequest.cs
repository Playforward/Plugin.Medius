using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyReport, MediusMGCLMessageIds.ServerJoinGameRequest)]
    public class MediusServerJoinGameRequest : BaseMGCLMessage, IMediusRequest
    {

		public override byte PacketType => (byte)MediusMGCLMessageIds.ServerJoinGameRequest;

        public MessageId MessageID { get; set; }
        public NetConnectionInfo ConnectInfo;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            ConnectInfo = reader.Read<NetConnectionInfo>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(ConnectInfo);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusServerJoinGameResponse()
            {
                MessageID = request.MessageID,
                Confirmation = MGCL_ERROR_CODE.MGCL_UNSUCCESSFUL
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"ConnectInfo:{ConnectInfo}";
        }
    }
}
