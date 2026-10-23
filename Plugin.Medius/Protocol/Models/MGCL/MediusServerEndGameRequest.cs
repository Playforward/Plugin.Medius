using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyReport, MediusMGCLMessageIds.ServerEndGameRequest)]
    public class MediusServerEndGameRequest : BaseMGCLMessage, IMediusRequest
    {

		public override byte PacketType => (byte)MediusMGCLMessageIds.ServerEndGameRequest;

        public MessageId MessageID { get; set; }
        public int WorldID;
        public bool BrutalFlag;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            WorldID = reader.ReadInt32();
            BrutalFlag = reader.ReadBoolean();
            reader.ReadBytes(3);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(WorldID);
            writer.Write(BrutalFlag);
            writer.Write(new byte[3]);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusServerEndGameResponse()
            {
                MessageID = request.MessageID,
                Confirmation = MGCL_ERROR_CODE.MGCL_UNSUCCESSFUL
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"WorldID:{WorldID} " +
                $"BrutalFlag:{BrutalFlag}";
        }
    }
}
