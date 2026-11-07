using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// A client asks about another player's connection (Resistance 2 over RUdp asks
    /// every few seconds for each player, itself included). Seen as <c>01 0000</c>,
    /// <c>01 0100</c>, <c>01 0500</c> [live PSRewired, MediusProxy 2026-10-29].
    /// </summary>
    [ScertMessage(RT_MSG_TYPE.RT_MSG_CLIENT_PEER_QUERY)]
    public class RT_MSG_CLIENT_PEER_QUERY : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_CLIENT_PEER_QUERY;

        /// <summary>Always 1 [live; meaning guess: the query kind].</summary>
        public byte QueryType = 1;
        public ushort PlayerIndex;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            QueryType = reader.ReadByte();
            PlayerIndex = reader.ReadUInt16();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(QueryType);
            writer.Write(PlayerIndex);
        }

        public override string ToString() => base.ToString() + $" QueryType:{QueryType} PlayerIndex:{PlayerIndex}";
    }
}
