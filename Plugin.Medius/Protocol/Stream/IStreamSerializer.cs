using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Shared
{
    public interface IStreamSerializer
    {
        void Serialize(Stream.MessageWriter writer);
        void Deserialize(Stream.MessageReader reader);

    }
}
