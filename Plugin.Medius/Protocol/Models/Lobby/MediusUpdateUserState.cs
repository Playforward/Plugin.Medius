using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.UpdateUserState)]
    public class MediusUpdateUserState : BaseLobbyMessage
    {


		public override byte PacketType => (byte)MediusLobbyMessageIds.UpdateUserState;

        public string SessionKey; // SESSIONKEY_MAXLEN
        public MediusUserAction UserAction;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            // 
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(3);
            UserAction = reader.Read<MediusUserAction>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            // 
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[3]);
            writer.Write(UserAction);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
             $"SessionKey:{SessionKey} " +
$"UserAction:{UserAction}";
        }
    }
}
