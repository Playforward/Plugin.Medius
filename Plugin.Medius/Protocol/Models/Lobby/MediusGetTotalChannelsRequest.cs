using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetTotalChannels)]
    public class MediusGetTotalChannelsRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.GetTotalChannels;

        public MessageId MessageID { get; set; }

        public string SessionKey;
        public int ApplicationId;


        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            ApplicationId = reader.ReadInt32();
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
            writer.Write(ApplicationId);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusGetTotalChannelsResponse()
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusNoResult
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"SessionKey:{SessionKey} " +
                $"ApplicationId:{ApplicationId}";
        }
    }
}
