using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using Plugin.Medius.RT.Shared.Stream;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// An app single with a quality-of-service byte in front (0x33): QoS byte, target index, payload.
    /// Resistance: Fall of Man's host answers a joiner this way, its DME ClientConnects (00 10) and
    /// its game hello (02 1D) both [live 2026-11-06: 50 01 00 00 10 .., 50 01 00 02 1D ..].
    /// </summary>
    [ScertMessage(RT_MSG_TYPE.RT_MSG_CLIENT_APP_SINGLE_QOS)]
    public class RT_MSG_CLIENT_APP_SINGLE_QOS : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_CLIENT_APP_SINGLE_QOS;

        public byte QosFlags { get; set; }
        public short Target { get; set; }
        public byte[] Payload { get; set; } = [];

        public override void Deserialize(MessageReader reader)
        {
            QosFlags = reader.ReadByte();
            Target = reader.ReadInt16();
            Payload = reader.ReadRest();
        }

        public override void Serialize(MessageWriter writer)
        {
            writer.Write(QosFlags);
            writer.Write(Target);
            writer.Write(Payload);
        }

        public override string ToString() => base.ToString() + $" QosFlags:0x{QosFlags:X2} Target:{Target} Contents:{BitConverter.ToString(Payload)}";
    }
}
