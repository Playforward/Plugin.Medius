using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.BinaryMessage)]
    public class MediusBinaryMessage : BaseLobbyExtMessage
    {

		public override byte PacketType => (byte)MediusLobbyExtMessageIds.BinaryMessage;

        public MessageId MessageID { get; set; }

        public string SessionKey; // SESSIONKEY_MAXLEN
        public MediusBinaryMessageType MessageType;
        public int TargetAccountID;
        public byte[] Message = new byte[Constants.BINARYMESSAGE_MAXLEN];

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            MessageType = reader.Read<MediusBinaryMessageType>();
            TargetAccountID = reader.ReadInt32();
            Message = reader.ReadBytes(Constants.BINARYMESSAGE_MAXLEN);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(MessageType);
            writer.Write(TargetAccountID);
            writer.Write(Message, Constants.BINARYMESSAGE_MAXLEN);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"SessionKey:{SessionKey} " +
                $"MessageType:{MessageType} " +
                $"TargetAccountID:{TargetAccountID} " +
                $"Message:{BitConverter.ToString(Message)}";
        }
    }
}
