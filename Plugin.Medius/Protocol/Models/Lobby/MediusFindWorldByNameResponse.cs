using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// One world found by <see cref="MediusFindWorldByNameRequest"/>, one
    /// message per world, the last with EndOfList. 144 bytes: MessageID (21),
    /// 3 pad, StatusCode, ApplicationID, ApplicationName (32), ApplicationType,
    /// MediusWorldID, WorldName (64), WorldStatus, EndOfList (1), 3 pad
    /// (Killzone 2 1.29: lobby 0x38, size 0x7295b8 returns 0x90, codec
    /// 0x7337d8) [C]. Killzone 2 keeps the MediusWorldID of each answer, so it
    /// joins the last one (0x4b9bd0) [C].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.FindWorldByNameResponse)]
    public class MediusFindWorldByNameResponse : BaseLobbyMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.FindWorldByNameResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public int ApplicationID;
        public string ApplicationName;
        public MediusApplicationType ApplicationType;
        public int MediusWorldID;
        public string WorldName;
        public MediusWorldStatus WorldStatus;
        public bool EndOfList;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            ApplicationID = reader.ReadInt32();
            ApplicationName = reader.ReadString(Constants.APPNAME_MAXLEN);
            ApplicationType = reader.Read<MediusApplicationType>();
            MediusWorldID = reader.ReadInt32();
            WorldName = reader.ReadString(Constants.WORLDNAME_MAXLEN);
            WorldStatus = reader.Read<MediusWorldStatus>();
            EndOfList = reader.ReadBoolean();
            reader.ReadBytes(3);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(ApplicationID);
            writer.Write(ApplicationName, Constants.APPNAME_MAXLEN);
            writer.Write(ApplicationType);
            writer.Write(MediusWorldID);
            writer.Write(WorldName, Constants.WORLDNAME_MAXLEN);
            writer.Write(WorldStatus);
            writer.Write(EndOfList);
            writer.Write(new byte[3]);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"StatusCode:{StatusCode} " +
                $"ApplicationID:{ApplicationID} " +
                $"ApplicationName:{ApplicationName} " +
                $"ApplicationType:{ApplicationType} " +
                $"MediusWorldID:{MediusWorldID} " +
                $"WorldName:{WorldName} " +
                $"WorldStatus:{WorldStatus} " +
                $"EndOfList:{EndOfList}";
        }
    }
}
