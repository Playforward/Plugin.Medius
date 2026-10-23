using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyReport, MediusMGCLMessageIds.ServerSessionEndRequest)]
    public class MediusServerSessionEndRequest : BaseMGCLMessage, IMediusRequest
    {
		public override byte PacketType => (byte)MediusMGCLMessageIds.ServerSessionEndRequest;

        public MessageId MessageID { get; set; }

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            MessageID = reader.Read<MessageId>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(MessageID ?? MessageId.Empty);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID}";
        }
    }
}
