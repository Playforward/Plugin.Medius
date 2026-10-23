using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.TicketLoginResponse)]
    public class MediusTicketLoginResponse : BaseLobbyExtMessage, IMediusResponse
    {

        public override byte PacketType => (byte)MediusLobbyExtMessageIds.TicketLoginResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public int AccountID;
        public MediusAccountType AccountType;
        public int MediusWorldID;
        public NetConnectionInfo ConnectInfo;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            reader.ReadBytes(29);
            AccountID = reader.ReadInt32();
            AccountType = reader.Read<MediusAccountType>();
            MediusWorldID = reader.ReadInt32();
            ConnectInfo = reader.Read<NetConnectionInfo>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(new byte[29]);
            writer.Write(AccountID);
            writer.Write(AccountType);
            writer.Write(MediusWorldID);
            writer.Write(ConnectInfo);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                    $"MessageID:{MessageID} " +
                    $"StatusCode:{StatusCode} " +
                    $"AccountID:{AccountID} " +
                    $"AccountType:{AccountType} " +
                    $"MediusWorldID:{MediusWorldID} " +
                    $"ConnectInfo:{ConnectInfo}";
        }
    }
}