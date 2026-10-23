using Plugin.Medius.RT.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [ScertMessage(RT_MSG_TYPE.RT_MSG_CLIENT_DISCONNECT_WITH_REASON)]
    public class RT_MSG_CLIENT_DISCONNECT_WITH_REASON : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_CLIENT_DISCONNECT_WITH_REASON;

        public byte Reason;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            Reason = reader.ReadByte();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(Reason);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"Reason:{Reason}";
        }
    }
}
