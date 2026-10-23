using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    public class MediusGenericChatFilter : IStreamSerializer
    {

        public byte[] GenericChatFilterBitfield = new byte[Constants.MEDIUS_GENERIC_CHAT_FILTER_BYTES_LEN];

        public void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            GenericChatFilterBitfield = reader.ReadBytes(Constants.MEDIUS_GENERIC_CHAT_FILTER_BYTES_LEN);
        }

        public void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            writer.Write(GenericChatFilterBitfield, Constants.MEDIUS_GENERIC_CHAT_FILTER_BYTES_LEN);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"GenericChatFilterBitfield:{BitConverter.ToString(GenericChatFilterBitfield)}";
        }
    }
}
