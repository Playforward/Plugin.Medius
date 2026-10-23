using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyReport, MediusMGCLMessageIds.ServerSetAttributesResponse)]
    public class MediusServerSetAttributesResponse : BaseMGCLMessage, IMediusResponse
    {

		public override byte PacketType => (byte)MediusMGCLMessageIds.ServerSetAttributesResponse;

        public MessageId MessageID { get; set; }
        public MGCL_ERROR_CODE Confirmation;

        public bool IsSuccess => Confirmation >= 0;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            MessageID = reader.Read<MessageId>();
            Confirmation = reader.Read<MGCL_ERROR_CODE>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(Confirmation);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"Confirmation:{Confirmation}";
        }
    }
}
