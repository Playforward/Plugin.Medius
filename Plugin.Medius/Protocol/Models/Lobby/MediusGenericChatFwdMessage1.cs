using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.GenericChatFwdMessage1)]
    public class MediusGenericChatFwdMessage1 : BaseLobbyExtMessage
    {


		public override byte PacketType => (byte)MediusLobbyExtMessageIds.GenericChatFwdMessage1;

        public uint TimeStamp;
        public int OriginatorAccountID;
        public MediusChatMessageType MessageType;
        public string OriginatorAccountName; // ACCOUNTNAME_MAXLEN
        public string Message; // CHATMESSAGE_MAXLEN

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            TimeStamp = reader.ReadUInt32();
            OriginatorAccountID = reader.ReadInt32();
            MessageType = reader.Read<MediusChatMessageType>();
            OriginatorAccountName = reader.ReadString(Constants.ACCOUNTNAME_MAXLEN);
            // (local fix: read the length-prefixed text the serializer writes)
            int len = reader.ReadInt32();
            Message = len > 0 ? reader.ReadString(len) : "";
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(TimeStamp);
            writer.Write(OriginatorAccountID);
            writer.Write(MessageType);
            writer.Write(OriginatorAccountName, Constants.ACCOUNTNAME_MAXLEN);
            if (Message == null || Message.Length == 0)
            {
                writer.Write(0);
            }
            else
            {
                int len = Message.Length;
                writer.Write(len + 1);
                writer.Write(Message, len + 1);
            }
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"TimeStamp:{TimeStamp} " +
                $"OriginatorAccountID:{OriginatorAccountID} " +
                $"MessageType:{MessageType} " +
                $"OriginatorAccountName:{OriginatorAccountName} " +
                $"Message:{Message}";
        }
    }
}
