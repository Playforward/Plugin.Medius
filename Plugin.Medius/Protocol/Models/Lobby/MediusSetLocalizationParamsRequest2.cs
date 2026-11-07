using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// The localization request of SCE-RT 3.98 (Resistance: Fall of Man disc 1.00, lobby-ext 0x5A,
    /// codec 0x61ff10): MessageID, SessionKey, pad 2, CharacterEncoding, Language and one more u32,
    /// 54 bytes [C]. The game sends UTF8 (2), USEnglish (1), 0 and PSRewired answers
    /// MediusSetLocalizationParamsResponse (lobby 0xA4) [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.SetLocalizationParams2)]
    public class MediusSetLocalizationParamsRequest2 : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.SetLocalizationParams2;

        public MessageId MessageID { get; set; }

        public string SessionKey; // SESSIONKEY_MAXLEN
        public MediusCharacterEncodingType CharacterEncoding;
        public MediusLanguageType Language;
        /// <summary>[guess] Unnamed u32 at +0x30 of the client's struct; 0 [live].</summary>
        public uint Unk30;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            CharacterEncoding = reader.Read<MediusCharacterEncodingType>();
            Language = reader.Read<MediusLanguageType>();
            Unk30 = reader.ReadUInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(CharacterEncoding);
            writer.Write(Language);
            writer.Write(Unk30);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) => new MediusSetLocalizationParamsResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusFail,
        };

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} SessionKey:{SessionKey} CharacterEncoding:{CharacterEncoding} Language:{Language} Unk30:{Unk30}";
    }
}
