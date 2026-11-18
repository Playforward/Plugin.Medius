using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// SCE-RT 3.06's lobby-ext 0x41: Killzone 3 sends it once, right after its MLS connect (state machine
    /// 0x15e694, state 10; API 0xa1b460, codec 0xa2f260), carrying only a MessageID [C; live 2026-11-08].
    /// Nothing waits on the answer. Name [guess].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.PostLoginQuery)]
    public class MediusPostLoginQueryRequest : BaseLobbyExtMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.PostLoginQuery;

        public MessageId MessageID { get; set; }

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) =>
            new MediusPostLoginQueryResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusFail };

        public override string ToString() => base.ToString() + $" MessageID:{MessageID}";
    }

    /// <summary>
    /// The answer to <see cref="MediusPostLoginQueryRequest"/>: MessageID, pad 3, StatusCode, then a u32 (raw 0x20,
    /// handler 0xa2d34c). With status 0 the client sets a flag (+0x68 of its login task) to Value == 4 [C 0x161694].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.PostLoginQueryResponse)]
    public class MediusPostLoginQueryResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.PostLoginQueryResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }
        public MediusCallbackStatus StatusCode;
        /// <summary>[guess] u32 at +0x1c; 4 sets the client's flag.</summary>
        public uint Value;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            Value = reader.ReadUInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(Value);
        }

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} StatusCode:{StatusCode} Value:{Value}";
    }
}
