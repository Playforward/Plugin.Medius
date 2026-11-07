using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// The DME ping (class DME, id 1). Resistance 2 sends it unreliably about every
    /// 0.4 s with <c>00000000 00 01 0000</c>; PSRewired answers each with the Unix time
    /// in seconds and zeros (<c>B4CBE26A 00 00 0000</c> at 2026-10-29 04:29:40 UTC)
    /// [live, MediusProxy]. Field names after Time are a guess.
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassDME, MediusDmeMessageIds.Ping)]
    public class DMETypePing : BaseDMEMessage
    {
        public override byte PacketType => (byte)MediusDmeMessageIds.Ping;

        public uint Time;
        public byte PingInstance;
        /// <summary>1 from the client, 0 in the server's answer [live].</summary>
        public byte RequestEcho;
        public ushort Unk06;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            Time = reader.ReadUInt32();
            PingInstance = reader.ReadByte();
            RequestEcho = reader.ReadByte();
            Unk06 = reader.ReadUInt16();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(Time);
            writer.Write(PingInstance);
            writer.Write(RequestEcho);
            writer.Write(Unk06);
        }

        public override string ToString() =>
            base.ToString() + $" Time:{Time} PingInstance:{PingInstance} RequestEcho:{RequestEcho} Unk06:{Unk06}";
    }
}
