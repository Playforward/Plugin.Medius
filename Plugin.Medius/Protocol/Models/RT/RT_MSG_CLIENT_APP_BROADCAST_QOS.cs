using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using Plugin.Medius.RT.Shared.Stream;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// An app broadcast with a quality-of-service byte in front (0x32). Resistance: Fall of Man sends
    /// its in-game state this way on the DME, over TCP (QoS 0x40/0x50) and aux UDP (0x00), and the
    /// payload after the byte starts with the game's own message byte (02) as in its plain app
    /// messages [live 2026-11-01]. That the QoS part is exactly one byte is a [guess] from those samples.
    /// </summary>
    [ScertMessage(RT_MSG_TYPE.RT_MSG_CLIENT_APP_BROADCAST_QOS)]
    public class RT_MSG_CLIENT_APP_BROADCAST_QOS : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_CLIENT_APP_BROADCAST_QOS;

        public byte QosFlags { get; set; }
        public byte[] Payload { get; set; } = [];

        public override void Deserialize(MessageReader reader)
        {
            QosFlags = reader.ReadByte();
            Payload = reader.ReadRest();
        }

        public override void Serialize(MessageWriter writer)
        {
            writer.Write(QosFlags);
            writer.Write(Payload);
        }

        public override string ToString() => base.ToString() + $" QosFlags:0x{QosFlags:X2} Contents:{BitConverter.ToString(Payload)}";
    }
}
