using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Tells a player someone invited them onto a buddy list. 64 bytes:
    /// MessageID (21), 3 pad, OriginatorAccountID, OriginatorAccountName (32),
    /// AddType (Killzone 2 1.29: lobby-ext 0x06, codec 0x731e50, handler
    /// 0x72c1b0 posts it to the game) [C]. The player answers with
    /// <see cref="MediusAddToBuddyListFwdConfirmationResponse"/>.
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.AddToBuddyListFwdConfirmation)]
    public class MediusAddToBuddyListFwdConfirmationRequest : BaseLobbyExtMessage
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.AddToBuddyListFwdConfirmation;

        public MessageId MessageID { get; set; }

        public int OriginatorAccountID;
        public string OriginatorAccountName; // ACCOUNTNAME_MAXLEN
        public MediusBuddyAddType AddType;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            OriginatorAccountID = reader.ReadInt32();
            OriginatorAccountName = reader.ReadString(Constants.ACCOUNTNAME_MAXLEN);
            AddType = reader.Read<MediusBuddyAddType>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(OriginatorAccountID);
            writer.Write(OriginatorAccountName, Constants.ACCOUNTNAME_MAXLEN);
            writer.Write(AddType);
        }

        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
                $"OriginatorAccountID:{OriginatorAccountID} " +
                $"OriginatorAccountName:{OriginatorAccountName} " +
                $"AddType:{AddType}";
        }
    }
}
