using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [ScertMessage(RT_MSG_TYPE.RT_MSG_SERVER_FORCED_DISCONNECT)]
    public class RT_MSG_SERVER_FORCED_DISCONNECT : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_SERVER_FORCED_DISCONNECT;

        public SERVER_FORCE_DISCONNECT_REASON Reason;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            Reason = reader.Read<SERVER_FORCE_DISCONNECT_REASON>();
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
