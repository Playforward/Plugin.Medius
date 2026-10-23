using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.InvitePlayerToClan_ByName)]
    public class MediusInvitePlayerToClan_ByNameRequest : BaseLobbyMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.InvitePlayerToClan_ByName;



        public MessageId MessageID { get; set; }
        public string AccountName; // ACCOUNTNAME_MAXLEN
        public string InviteMessage; // CLANMSG_MAXLEN

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            MessageID = reader.Read<MessageId>();
            AccountName = reader.ReadString(Constants.ACCOUNTNAME_MAXLEN);
            InviteMessage = reader.ReadString(Constants.CLANMSG_MAXLEN);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(AccountName, Constants.ACCOUNTNAME_MAXLEN);
            writer.Write(InviteMessage, Constants.CLANMSG_MAXLEN);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID}" + " " +
                $"AccountName:{AccountName}" + " " +
                $"InviteMessage:{InviteMessage}";
        }
    }
}