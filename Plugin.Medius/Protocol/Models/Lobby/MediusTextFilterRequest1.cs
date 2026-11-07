using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System.Text;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Text check of SCE-RT 3.02 (lobby-ext 0x75; Resistance: Fall of Man patch codec 0x658460):
    /// MessageID, SessionKey, FilterType u32, TextLength u32, Text[TextLength] with no pad after the
    /// session key [C]. RFOM sends FilterType 1 and the length without a terminator ("test" = 4; 47-52
    /// bytes in all) [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MediusTextFilter1)]
    public class MediusTextFilterRequest1 : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MediusTextFilter1;

        public MessageId MessageID { get; set; }

        public string SessionKey; // SESSIONKEY_MAXLEN
        /// <summary>[guess] What the text is checked as; 1 [live].</summary>
        public uint FilterType;
        public string Text = ""; // TextLength bytes

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            FilterType = reader.ReadUInt32();
            int length = reader.ReadInt32();
            Text = Encoding.UTF8.GetString(reader.ReadBytes(length)).TrimEnd('\0');
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            byte[] text = Encoding.UTF8.GetBytes(Text ?? "");
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(FilterType);
            writer.Write(text.Length);
            writer.Write(text);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) => new MediusTextFilterResponse1
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusFail,
        };

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} SessionKey:{SessionKey} FilterType:{FilterType} Text:{Text}";
    }
}
