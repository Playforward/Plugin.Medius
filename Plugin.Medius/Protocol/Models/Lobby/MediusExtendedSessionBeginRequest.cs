using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.ExtendedSessionBeginRequest)]
    public class MediusExtendedSessionBeginRequest : BaseLobbyExtMessage, IMediusRequest
    {


		public override byte PacketType => (byte)MediusLobbyExtMessageIds.ExtendedSessionBeginRequest;

        public MessageId MessageID { get; set; }

        public int ClientVersionMajor;
        public int ClientVersionMinor;
        public int ClientVersionBuild;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            ClientVersionMajor = reader.ReadInt32();
            ClientVersionMinor = reader.ReadInt32();
            ClientVersionBuild = reader.ReadInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(ClientVersionMajor);
            writer.Write(ClientVersionMinor);
            writer.Write(ClientVersionBuild);
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
$"ClientVersionMajor:{ClientVersionMajor} " +
$"ClientVersionMinor:{ClientVersionMinor} " +
$"ClientVersionBuild:{ClientVersionBuild}";
        }
    }
}
