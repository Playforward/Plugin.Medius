using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.RequestClanTeamChallenge)]
    public class MediusRequestClanTeamChallengeRequest : BaseLobbyMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.RequestClanTeamChallenge;



        public MessageId MessageID { get; set; }
        public string SessionKey; // SESSIONKEY_MAXLEN
        public int ClanID;
        public string Message; // CLANMSG_MAXLEN

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            ClanID = reader.ReadInt32();
            Message = reader.ReadString(Constants.CLANMSG_MAXLEN);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(ClanID);
            writer.Write(Message, Constants.CLANMSG_MAXLEN);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID}" + " " +
                $"SessionKey:{SessionKey}" + " " +
                $"ClanID:{ClanID}" + " " +
                $"Message:{Message}";
        }
    }
}