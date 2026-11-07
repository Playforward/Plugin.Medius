using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// A variable-length binary message to another account. Client serializer
    /// (Resistance 2 EBOOT 0x80a8a8) [C]: MessageID, SessionKey, then (no pad)
    /// MessageType, TargetAccountID and Length as u32, then Length bytes.
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.BinaryMessage1)]
    public class MediusBinaryMessage1 : BaseLobbyExtMessage
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.BinaryMessage1;

        public MessageId MessageID { get; set; }
        public string SessionKey;
        public MediusBinaryMessageType MessageType;
        public int TargetAccountID;
        public byte[] Message = [];

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            MessageType = reader.Read<MediusBinaryMessageType>();
            TargetAccountID = reader.ReadInt32();
            Message = reader.ReadBytes(reader.ReadInt32());
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(MessageType);
            writer.Write(TargetAccountID);
            writer.Write((Message ?? []).Length);
            writer.Write(Message ?? []);
        }

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} SessionKey:{SessionKey} MessageType:{MessageType} TargetAccountID:{TargetAccountID} Message:{Convert.ToHexString(Message ?? [])}";
    }

    /// <summary>
    /// A binary message delivered to its target. Client serializer 0x80a7f0 [C]:
    /// MessageID, then (no pad) MessageType, OriginatorAccountID and Length as u32,
    /// then Length bytes.
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.BinaryFwdMessage1)]
    public class MediusBinaryFwdMessage1 : BaseLobbyExtMessage
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.BinaryFwdMessage1;

        public MessageId MessageID { get; set; }
        public MediusBinaryMessageType MessageType;
        public int OriginatorAccountID;
        public byte[] Message = [];

        /// <summary>
        /// Whether OriginatorAccountID comes before MessageType on the wire.
        /// The client's swap (R2 1.00 0x80a7f0) reads two u32s after MessageID
        /// without naming them; PSRewired's R2 server sends the originator
        /// first (account, then 1 = TargetBinaryMsg) [live R2 1.60]. Horizon's
        /// order (type first) stays the default; ScertLayout picks per library.
        /// </summary>
        public bool OriginatorFirst;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            if (OriginatorFirst)
            {
                OriginatorAccountID = reader.ReadInt32();
                MessageType = reader.Read<MediusBinaryMessageType>();
            }
            else
            {
                MessageType = reader.Read<MediusBinaryMessageType>();
                OriginatorAccountID = reader.ReadInt32();
            }
            Message = reader.ReadBytes(reader.ReadInt32());
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            if (OriginatorFirst)
            {
                writer.Write(OriginatorAccountID);
                writer.Write(MessageType);
            }
            else
            {
                writer.Write(MessageType);
                writer.Write(OriginatorAccountID);
            }
            writer.Write((Message ?? []).Length);
            writer.Write(Message ?? []);
        }

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} MessageType:{MessageType} OriginatorAccountID:{OriginatorAccountID} Message:{Convert.ToHexString(Message ?? [])}";
    }
}
