using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [MediusMessage(NetMessageTypes.MessageClassLobbyReport, MediusMGCLMessageIds.ServerAuthenticationRequest)]
    public class MediusServerAuthenticationRequest : BaseMGCLMessage, IMediusRequest
    {

        public override byte PacketType => (byte)MediusMGCLMessageIds.ServerAuthenticationRequest;

        public MessageId MessageID { get; set; }
        public MGCL_TRUST_LEVEL TrustLevel;
        public NetAddressList AddressList;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            TrustLevel = reader.Read<MGCL_TRUST_LEVEL>();
            AddressList = reader.Read<NetAddressList>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(TrustLevel);
            writer.Write(AddressList);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusServerAuthenticationResponse()
            {
                MessageID = request.MessageID,
                Confirmation = MGCL_ERROR_CODE.MGCL_UNSUCCESSFUL
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"TrustLevel:{TrustLevel} " +
                $"AddressList:{AddressList}";
        }
    }
}
