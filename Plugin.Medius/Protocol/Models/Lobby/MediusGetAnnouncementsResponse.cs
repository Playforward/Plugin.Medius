using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetAnnouncementsResponse)]
    public class MediusGetAnnouncementsResponse : BaseLobbyMessage, IMediusResponse
    {
		public override byte PacketType => (byte)MediusLobbyMessageIds.GetAnnouncementsResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public int AnnouncementID;
        public string Announcement; // AnnouncementLength
        public bool EndOfList;

        /// <summary>
        /// Layout switch, not a field: bytes of <see cref="Announcement"/> on the wire (1000;
        /// 4000 in SCE-RT 3.06, Killzone 3 codec 0xa34950) [C]. Set from the title's
        /// <see cref="ScertLayout.AnnouncementLength"/>.
        /// </summary>
        public int AnnouncementLength = Constants.ANNOUNCEMENT_MAXLEN;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            AnnouncementID = reader.ReadInt32();
            Announcement = reader.ReadString(AnnouncementLength);
            EndOfList = reader.ReadBoolean();
            reader.ReadBytes(3);
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
            writer.Write(AnnouncementID);
            writer.Write(Announcement, AnnouncementLength);
            writer.Write(EndOfList);
            writer.Write(new byte[3]);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
             $"StatusCode:{StatusCode} " +
$"AnnouncementID:{AnnouncementID} " +
$"Announcement:{Announcement} " +
$"EndOfList:{EndOfList}";
        }
    }
}
