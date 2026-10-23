using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetClanByName)]
    public class MediusGetClanByNameRequest : BaseLobbyMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.GetClanByName;



        public MessageId MessageID { get; set; }
        public string SessionKey; // SESSIONKEY_MAXLEN
        public int ApplicationID;
        public string ClanName; // CLANNAME_MAXLEN

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            ApplicationID = reader.ReadInt32();
            ClanName = reader.ReadString(Constants.CLANNAME_MAXLEN);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(ApplicationID);
            writer.Write(ClanName, Constants.CLANNAME_MAXLEN);
        }


        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusGetClanByNameResponse()
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusNoResult
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID}" + " " +
                $"SessionKey:{SessionKey}" + " " +
                $"ApplicationID:{ApplicationID}" + " " +
                $"ClanName:{ClanName}";
        }
    }
}