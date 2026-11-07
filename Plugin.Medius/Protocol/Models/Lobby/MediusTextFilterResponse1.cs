using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Answer to <see cref="MediusTextFilterRequest1"/> (lobby-ext 0x76; Resistance: Fall of Man
    /// patch codec 0x65850c, struct 0x24): MessageID, StatusCode (+0x20), TextLength (+0x18), then
    /// TextLength bytes of text read into the struct's inline buffer [C]. PSRewired answers status 0,
    /// length with the terminator and pads the message to 513 bytes, which the client does not
    /// read [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MediusTextFilterResponse1)]
    public class MediusTextFilterResponse1 : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MediusTextFilterResponse1;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        /// <summary>The (filtered) text; written with its terminator, as the client counts it.</summary>
        public string Text = "";

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            StatusCode = reader.Read<MediusCallbackStatus>();
            int length = reader.ReadInt32();
            Text = Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0');
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            byte[] text = Encoding.UTF8.GetBytes((Text ?? "") + "\0");
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(StatusCode);
            writer.Write(text.Length);
            writer.Write(text);
        }

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} StatusCode:{StatusCode} Text:{Text}";
    }
}
