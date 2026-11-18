using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using Plugin.Medius.RT.Shared.Stream;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// An app list with a quality-of-service byte in front (0x34). Not seen live: [guess] the QoS byte
    /// of <see cref="RT_MSG_CLIENT_APP_BROADCAST_QOS"/> and <see cref="RT_MSG_CLIENT_APP_SINGLE_QOS"/>
    /// followed by the layout of <see cref="RT_MSG_CLIENT_APP_LIST"/>.
    /// </summary>
    [ScertMessage(RT_MSG_TYPE.RT_MSG_CLIENT_APP_LIST_QOS)]
    public class RT_MSG_CLIENT_APP_LIST_QOS : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_CLIENT_APP_LIST_QOS;

        public byte QosFlags { get; set; }
        public List<int> Targets { get; set; } = [];
        public byte[] Payload { get; set; } = [];

        public override void Deserialize(MessageReader reader)
        {
            QosFlags = reader.ReadByte();
            byte size = reader.ReadByte();
            byte[] mask = reader.ReadBytes(size);
            Payload = reader.ReadRest();

            Targets = [];
            for (int b = 0; b < size; ++b)
                for (int i = 0; i < 8; ++i)
                    if ((mask[b] & (1 << i)) != 0)
                        Targets.Add(i + (b * 8));
        }

        public override void Serialize(MessageWriter writer)
        {
            byte size = Targets.Count > 0 ? (byte)Math.Ceiling((Targets.Max() + 1) / 8d) : (byte)1;
            byte[] mask = new byte[size];
            foreach (int target in Targets)
                mask[target / 8] |= (byte)(1 << (target % 8));

            writer.Write(QosFlags);
            writer.Write(size);
            writer.Write(mask);
            writer.Write(Payload);
        }

        public override string ToString() => base.ToString() + $" QosFlags:0x{QosFlags:X2} Targets:{string.Join(",", Targets)} Contents:{BitConverter.ToString(Payload)}";
    }
}
