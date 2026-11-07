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

        // Layout from Resistance: Fall of Man's codec 0x6633a0 (patched; disc 0x6209c8): the
        // SessionBegin struct 0x662688 (MessageID, pad 3, ConnectionClass) then three u32 [C].
        // RFOM sends ConnectionClass 1, version 3.98 (disc) or 3.2 (patch) [live].
        public MessageId MessageID { get; set; }

        public MediusConnectionType ConnectionClass;
        public int ClientVersionMajor;
        public int ClientVersionMinor;
        public int ClientVersionBuild;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            ConnectionClass = reader.Read<MediusConnectionType>();
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
            writer.Write(new byte[3]);
            writer.Write(ConnectionClass);
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
$"ConnectionClass:{ConnectionClass} " +
$"ClientVersionMajor:{ClientVersionMajor} " +
$"ClientVersionMinor:{ClientVersionMinor} " +
$"ClientVersionBuild:{ClientVersionBuild}";
        }
    }
}
