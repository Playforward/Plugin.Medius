using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.SessionBegin1)]
    public class MediusSessionBegin1Request : BaseLobbyExtMessage, IMediusRequest
    {
		public override byte PacketType => (byte)MediusLobbyExtMessageIds.SessionBegin1;

        public MessageId MessageID { get; set; }

        public MediusConnectionType ConnectionClass;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            reader.ReadBytes(3);
            ConnectionClass = reader.Read<MediusConnectionType>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(new byte[3]);
            writer.Write(ConnectionClass);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusSessionBeginResponse()
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusFail
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " + 
                $"MessageID:{MessageID} " +
                $"ConnectionClass:{ConnectionClass}";
        }
    }
}
