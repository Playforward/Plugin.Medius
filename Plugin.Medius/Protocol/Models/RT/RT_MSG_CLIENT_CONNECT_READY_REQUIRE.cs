using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    [ScertMessage(RT_MSG_TYPE.RT_MSG_CLIENT_CONNECT_READY_REQUIRE)]
    public class RT_MSG_CLIENT_CONNECT_READY_REQUIRE : BaseScertMessage
    {

        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_CLIENT_CONNECT_READY_REQUIRE;

        // 
        public byte ARG1 = 0x00;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            ARG1 = reader.ReadByte();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(ARG1);
        }
    }
}
