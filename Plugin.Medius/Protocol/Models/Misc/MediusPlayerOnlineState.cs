
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    public class MediusPlayerOnlineState : IStreamSerializer
    {
        public static readonly MediusPlayerOnlineState Empty = new MediusPlayerOnlineState();

        public MediusPlayerStatus ConnectStatus;
        public int MediusLobbyWorldID;
        public int MediusGameWorldID;
        public string LobbyName;
        public string GameName;

        public void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            ConnectStatus = reader.Read<MediusPlayerStatus>();
            MediusLobbyWorldID = reader.ReadInt32();
            MediusGameWorldID = reader.ReadInt32();
            LobbyName = reader.ReadString(Constants.WORLDNAME_MAXLEN);
            GameName = reader.ReadString(Constants.WORLDNAME_MAXLEN);
        }

        public void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(ConnectStatus);
            writer.Write(MediusLobbyWorldID);
            writer.Write(MediusGameWorldID);
            writer.Write(LobbyName, Constants.WORLDNAME_MAXLEN);
            writer.Write(GameName, Constants.WORLDNAME_MAXLEN);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"ConnectStatus:{ConnectStatus} " +
                $"MediusLobbyWorldID:{MediusLobbyWorldID} " +
                $"MediusGameWorldID:{MediusGameWorldID} " +
                $"LobbyName:{LobbyName} " +
                $"GameName:{GameName}";
        }
    }
}
