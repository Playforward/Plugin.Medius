using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetTotalRankings)]
    public class MediusGetTotalRankingsRequest : BaseLobbyMessage, IMediusRequest
    {
		public override byte PacketType => (byte)MediusLobbyMessageIds.GetTotalRankings;

        public MessageId MessageID { get; set; }

        public MediusLadderType LadderType;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            reader.ReadBytes(3);
            LadderType = reader.Read<MediusLadderType>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(new byte[3]);
            writer.Write(LadderType);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusGetTotalRankingsResponse()
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusNoResult
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
             $"LadderType:{LadderType}";
        }
    }
}
