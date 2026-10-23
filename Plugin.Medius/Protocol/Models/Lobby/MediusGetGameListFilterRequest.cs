using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetGameListFilter)]
    public class MediusGetGameListFilterRequest : BaseLobbyMessage, IMediusRequest
    {
		public override byte PacketType => (byte)MediusLobbyMessageIds.GetGameListFilter;

        public MessageId MessageID { get; set; }

        public string SessionKey; // SESSIONKEY_MAXLEN

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusGetGameListFilterResponse()
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
             $"SessionKey:{SessionKey}";
        }
    }
}
