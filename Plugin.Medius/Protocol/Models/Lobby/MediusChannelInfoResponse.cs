using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.ChannelInfoResponse)]
    public class MediusChannelInfoResponse : BaseLobbyMessage, IMediusResponse
    {

		public override byte PacketType => (byte)MediusLobbyMessageIds.ChannelInfoResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public string LobbyName; // LOBBYNAME_MAXLEN
        public int ActivePlayerCount;
        public int MaxPlayers;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            LobbyName = reader.ReadString(Constants.LOBBYNAME_MAXLEN);
            ActivePlayerCount = reader.ReadInt32();
            MaxPlayers = reader.ReadInt32();
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
            writer.Write(LobbyName, Constants.LOBBYNAME_MAXLEN);
            writer.Write(ActivePlayerCount);
            writer.Write(MaxPlayers);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
             $"StatusCode:{StatusCode} " +
$"LobbyName:{LobbyName} " +
$"ActivePlayerCount:{ActivePlayerCount} " +
$"MaxPlayers:{MaxPlayers}";
        }
    }
}
