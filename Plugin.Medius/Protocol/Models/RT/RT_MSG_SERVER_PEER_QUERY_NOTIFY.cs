using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// The answer to <see cref="RT_MSG_CLIENT_PEER_QUERY"/>: the query repeated, then
    /// three latencies in milliseconds. PSRewired gives every player of one query
    /// round the same three values, which fit max/min/average of the asking
    /// client's own round trip (93/79/83, 120/78/93, 759/4/102) [live; meaning guess].
    /// </summary>
    [ScertMessage(RT_MSG_TYPE.RT_MSG_SERVER_PEER_QUERY_NOTIFY)]
    public class RT_MSG_SERVER_PEER_QUERY_NOTIFY : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_SERVER_PEER_QUERY_NOTIFY;

        public byte QueryType = 1;
        public ushort PlayerIndex;
        public ushort MaxLatency;
        public ushort MinLatency;
        public ushort AverageLatency;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            QueryType = reader.ReadByte();
            PlayerIndex = reader.ReadUInt16();
            MaxLatency = reader.ReadUInt16();
            MinLatency = reader.ReadUInt16();
            AverageLatency = reader.ReadUInt16();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(QueryType);
            writer.Write(PlayerIndex);
            writer.Write(MaxLatency);
            writer.Write(MinLatency);
            writer.Write(AverageLatency);
        }

        public override string ToString() =>
            base.ToString() + $" QueryType:{QueryType} PlayerIndex:{PlayerIndex} Max:{MaxLatency} Min:{MinLatency} Avg:{AverageLatency}";
    }
}
