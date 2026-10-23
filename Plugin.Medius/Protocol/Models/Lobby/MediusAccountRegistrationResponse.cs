using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.AccountRegistrationResponse)]
    public class MediusAccountRegistrationResponse : BaseLobbyMessage, IMediusResponse
    {

		public override byte PacketType => (byte)MediusLobbyMessageIds.AccountRegistrationResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public int AccountID;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            AccountID = reader.ReadInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(AccountID);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
             $"StatusCode:{StatusCode} " +
$"AccountID:{AccountID}";
        }
    }
}
