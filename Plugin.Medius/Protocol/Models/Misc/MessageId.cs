using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    public class MessageId : IStreamSerializer
    {
        public readonly static MessageId Empty = new MessageId("");

        public string Value { get; protected set; }

        public MessageId()
        {
            Value = GenerateMessageId();
        }

        public MessageId(string value)
        {
            this.Value = value;
        }

        #region Serialization

        public void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            Value = reader.ReadString(Constants.MESSAGEID_MAXLEN);
        }

        public void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(Value, Constants.MESSAGEID_MAXLEN);
        }

        #endregion

        public override string ToString()
        {
            return Value;
        }

        private static string GenerateMessageId()
        {
            return "1";
        }
    }
}
