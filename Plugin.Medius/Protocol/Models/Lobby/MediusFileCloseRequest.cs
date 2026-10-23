using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.FileClose)]
    public class MediusFileCloseRequest : BaseLobbyMessage, IMediusRequest
    {

		public override byte PacketType => (byte)MediusLobbyMessageIds.FileClose;

        public MessageId MessageID { get; set; }

        public MediusFile MediusFileInfo;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            MediusFileInfo = reader.Read<MediusFile>();

            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            writer.Write(MediusFileInfo);

            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusFileCloseResponse()
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusFail
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"MediusFileInfo:{MediusFileInfo}";
        }
    }
}
