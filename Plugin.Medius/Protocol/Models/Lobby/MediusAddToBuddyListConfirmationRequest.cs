using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Invites a player onto the sender's buddy list. 48 bytes: MessageID (21),
    /// SessionKey (17), 2 pad, TargetAccountID, AddType (Killzone 2 1.29:
    /// lobby-ext 0x63, codec 0x731f80, sender 0x7197b8; the same codec is
    /// registered for 0x05) [C]. Answered by
    /// <see cref="MediusAddToBuddyListConfirmationResponse"/>; the target is told
    /// with <see cref="MediusAddToBuddyListFwdConfirmationRequest"/>.
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.AddToBuddyListConfirmationRequest)]
    public class MediusAddToBuddyListConfirmationRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.AddToBuddyListConfirmationRequest;

        public MessageId MessageID { get; set; }

        public string SessionKey; // SESSIONKEY_MAXLEN
        public int TargetAccountID;
        public MediusBuddyAddType AddType;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            TargetAccountID = reader.ReadInt32();
            AddType = reader.Read<MediusBuddyAddType>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(TargetAccountID);
            writer.Write(AddType);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request)
        {
            if (request == null)
                return null;

            return new MediusAddToBuddyListConfirmationResponse()
            {
                MessageID = request.MessageID,
                StatusCode = MediusCallbackStatus.MediusFail
            };
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"SessionKey:{SessionKey} " +
                $"TargetAccountID:{TargetAccountID} " +
                $"AddType:{AddType}";
        }
    }
}
