using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using Plugin.Medius.RT.Shared.Stream;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// The client asks the server to send what it has queued (0x0F, no body). Resistance: Fall of Man
    /// sends it on the DME over TCP and UDP [live]; the server sends at once, so there is nothing to do.
    /// </summary>
    [ScertMessage(RT_MSG_TYPE.RT_MSG_CLIENT_FLUSH_ALL)]
    public class RT_MSG_CLIENT_FLUSH_ALL : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_CLIENT_FLUSH_ALL;

        public byte[] Contents { get; set; } = [];

        public override void Deserialize(MessageReader reader) => Contents = reader.ReadRest();

        public override void Serialize(MessageWriter writer) => writer.Write(Contents);
    }
}
