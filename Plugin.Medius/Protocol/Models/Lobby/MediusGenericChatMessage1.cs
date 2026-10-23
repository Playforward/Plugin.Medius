using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models.Misc;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.GenericChatMessage1)]
    public class MediusGenericChatMessage1 : BaseLobbyExtMessage, IMediusChatMessage
    {
		public override byte PacketType => (byte)MediusLobbyExtMessageIds.GenericChatMessage1;

        public MessageId MessageID { get; set; }

        public string SessionKey; // SESSIONKEY_MAXLEN
        public MediusChatMessageType MessageType { get; set; }
        public int TargetID { get; set; }
        public string Message { get; set; }

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            MessageType = reader.Read<MediusChatMessageType>();
            TargetID = reader.ReadInt32();
            int length = reader.ReadInt32();
            Message = reader.ReadString(length);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(MessageType);
            writer.Write(TargetID);
            writer.Write(Message.Length);
            writer.Write(Message, Message.Length);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"SessionKey:{SessionKey} " +
                $"MessageType:{MessageType} " +
                $"TargetID:{TargetID} " +
                $"Message:{Message}";
        }
    }
}
