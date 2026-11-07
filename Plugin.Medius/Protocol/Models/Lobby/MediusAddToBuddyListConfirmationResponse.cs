using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Answer to <see cref="MediusAddToBuddyListConfirmationRequest"/>. 64
    /// bytes: MessageID (21), 3 pad, StatusCode, TargetAccountID,
    /// TargetAccountName (32) (Killzone 2 1.29: lobby-ext 0x64, codec 0x731ee8,
    /// also lobby 0x44's; handler 0x7297b0 completes the request by MessageID,
    /// so exactly one answer per request) [C].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.AddToBuddyListConfirmationResponse)]
    public class MediusAddToBuddyListConfirmationResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.AddToBuddyListConfirmationResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public int TargetAccountID;
        public string TargetAccountName; // ACCOUNTNAME_MAXLEN

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            TargetAccountID = reader.ReadInt32();
            TargetAccountName = reader.ReadString(Constants.ACCOUNTNAME_MAXLEN);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(TargetAccountID);
            writer.Write(TargetAccountName, Constants.ACCOUNTNAME_MAXLEN);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"StatusCode:{StatusCode} " +
                $"TargetAccountID:{TargetAccountID} " +
                $"TargetAccountName:{TargetAccountName}";
        }
    }
}
