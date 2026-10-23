using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyReport, MediusMGCLMessageIds.ServerEndGameOnMeRequest)]
    public class MediusServerEndGameOnMeRequest : BaseMGCLMessage, IMediusRequest
    {

		public override byte PacketType => (byte)MediusMGCLMessageIds.ServerEndGameOnMeRequest;

        public MessageId MessageID { get; set; }
        public int MediusWorldID;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            MediusWorldID = reader.ReadInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(MediusWorldID);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusServerEndGameOnMeResponse()
            {
                MessageID = request.MessageID,
                Confirmation = MGCL_ERROR_CODE.MGCL_UNSUCCESSFUL
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"MediusWorldID:{MediusWorldID}";
        }
    }
}
