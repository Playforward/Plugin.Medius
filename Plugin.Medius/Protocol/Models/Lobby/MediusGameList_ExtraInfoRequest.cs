using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.GameList_ExtraInfo)]
    public class MediusGameList_ExtraInfoRequest : BaseLobbyExtMessage, IMediusRequest
    {

		public override byte PacketType => (byte)MediusLobbyExtMessageIds.GameList_ExtraInfo;

        public MessageId MessageID { get; set; }

        public ushort PageID;
        public ushort PageSize;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            //
            reader.ReadBytes(1);
            PageID = reader.ReadUInt16();
            PageSize = reader.ReadUInt16();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(new byte[1]);
            writer.Write(PageID);
            writer.Write(PageSize);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusGameList_ExtraInfoResponse()
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusNoResult,
                EndOfList = true
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
             $"PageID:{PageID} " +
$"PageSize:{PageSize}";
        }
    }
}
