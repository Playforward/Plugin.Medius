using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Asks for a lobby to go to (lobby-ext 0x71, patch codec 0x657b70): MessageID, SessionKey, a u32
    /// [C]. Resistance: Fall of Man sends it from the match server to find its way back to the MLS
    /// (lobby_roomMgr 0x334e68, which zeroes the whole request, so the u32 is 0 [C]).
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.JoinLeastPopulatedChannel)]
    public class MediusJoinLeastPopulatedChannelRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.JoinLeastPopulatedChannel;

        public MessageId MessageID { get; set; }
        public string SessionKey; // SESSIONKEY_MAXLEN
        /// <summary>[guess] u32 at +0x28.</summary>
        public uint Unk28;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            Unk28 = reader.ReadUInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(Unk28);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) => new MediusJoinLeastPopulatedChannelResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusFail,
        };

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} SessionKey:{SessionKey} Unk28:{Unk28}";
    }
}
