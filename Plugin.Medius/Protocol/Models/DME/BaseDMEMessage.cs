using Plugin.Medius.RT.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    public abstract class BaseDMEMessage : BaseMediusMessage
    {
        public override NetMessageTypes PacketClass => NetMessageTypes.MessageClassDME;

        public BaseDMEMessage()
        {

        }
    }
}
