using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetWorldSecurityLevel)]
    public class MediusGetWorldSecurityLevelRequest : BaseLobbyMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.GetWorldSecurityLevel;

        /// <summary>
        /// Message ID
        /// </summary>
        public MessageId MessageID { get; set; }
        /// <summary>
        /// Session Key
        /// </summary>
        public string SessionKey; // SESSIONKEY_MAXLEN
        /// <summary>
        /// World ID to get the security level for.
        /// </summary>
        public int MediusWorldID;
        /// <summary>
        /// Application Type:lobby chat channel or game world.  
        /// </summary>
        public MediusApplicationType AppType;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(2);

            //
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);

            MediusWorldID = reader.ReadInt32();
            AppType = reader.Read<MediusApplicationType>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[2]);

            // 
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(MediusWorldID);
            writer.Write(AppType);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;


            return null;
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID: {MessageID} " +
                $"SessionKey: {SessionKey} " +
                $"MediusWorldID: {MediusWorldID} " +
                $"AppType: {AppType}";
        }
    }
}