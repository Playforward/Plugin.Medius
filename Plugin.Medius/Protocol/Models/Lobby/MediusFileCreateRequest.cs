using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.FileCreate)]
    public class MediusFileCreateRequest : BaseLobbyMessage, IMediusRequest
    {

		public override byte PacketType => (byte)MediusLobbyMessageIds.FileCreate;

        public MessageId MessageID { get; set; }

        public MediusFile MediusFileToCreate;
        public MediusFileAttributes MediusFileCreateAttributes;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            MediusFileToCreate = reader.Read<MediusFile>();
            MediusFileCreateAttributes = reader.Read<MediusFileAttributes>();

            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            writer.Write(MediusFileToCreate);
            writer.Write(MediusFileCreateAttributes);

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

            return new MediusFileCreateResponse()
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusFail
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
             $"MediusFileToCreate:{MediusFileToCreate} " +
$"MediusFileCreateAttributes:{MediusFileCreateAttributes}";
        }
    }
}
