using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [ScertMessage(RT_MSG_TYPE.RT_MSG_CLIENT_CONNECT_TCP)]
    public class RT_MSG_CLIENT_CONNECT_TCP : BaseScertMessage
    {
        public override RT_MSG_TYPE Id => RT_MSG_TYPE.RT_MSG_CLIENT_CONNECT_TCP;

        // 
        public uint TargetWorldId;
        public byte UNK0;
        public int AppId;
        public RSA_KEY Key;

        public string SessionKey = null;
        public string AccessToken = null;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            SessionKey = null;
            AccessToken = null;

            TargetWorldId = reader.ReadUInt32();
            if (reader.MediusVersion < 109)
                UNK0 = reader.ReadByte();
            AppId = reader.ReadInt32();
            Key = reader.Read<RSA_KEY>();

            if (reader.BaseStream.Position < reader.BaseStream.Length)
            {
                SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
                AccessToken = reader.ReadString(Constants.NET_ACCESS_KEY_LEN);
            }
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            writer.Write(TargetWorldId);
            if (writer.MediusVersion < 109)
                writer.Write(UNK0);
            writer.Write(AppId);
            writer.Write(Key ?? RSA_KEY.Empty);

            // (local addition: upstream only read these, so a client built
            // on these models could not resume a session)
            if (SessionKey is not null || AccessToken is not null)
            {
                writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
                writer.Write(AccessToken, Constants.NET_ACCESS_KEY_LEN);
            }
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"TargetWorldId:{TargetWorldId:X8} " +
                $"UNK0:{UNK0:X2} " +
                $"AppId:{AppId:X8} " +
                $"Key:{Key}" +
                $"SessionKey:{SessionKey}" +
                $"AccessToken:{AccessToken}"
                ;
        }
    }
}
