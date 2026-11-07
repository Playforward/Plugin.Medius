using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// How many users are online. Client serializer (Resistance 2 EBOOT 0x811e38) [C]:
    /// MessageID, SessionKey, 2 pad bytes, then a u32.
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetTotalUsers)]
    public class MediusGetTotalUsersRequest : BaseLobbyMessage, IMediusRequest
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.GetTotalUsers;

        public MessageId MessageID { get; set; }
        public string SessionKey;
        /// <summary>[guess] u32 at +0x28 (an application id?).</summary>
        public uint Unk28;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            SessionKey = reader.ReadString(Constants.SESSIONKEY_MAXLEN);
            reader.ReadBytes(2);
            Unk28 = reader.ReadUInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(SessionKey, Constants.SESSIONKEY_MAXLEN);
            writer.Write(new byte[2]);
            writer.Write(Unk28);
        }

        public IMediusResponse GetDefaultFailedResponse(IMediusRequest request) =>
            new MediusGetTotalUsersResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusFail };

        public override string ToString() => base.ToString() + $" MessageID:{MessageID} SessionKey:{SessionKey} Unk28:{Unk28}";
    }

    /// <summary>Client serializer 0x817988 [C]: MessageID, 3 pad bytes, StatusCode, two u32.</summary>
    [MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetTotalUsersResponse)]
    public class MediusGetTotalUsersResponse : BaseLobbyMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyMessageIds.GetTotalUsersResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }
        public MediusCallbackStatus StatusCode;
        /// <summary>[guess] Users logged in to the title.</summary>
        public uint TotalInSystem;
        /// <summary>[guess] Users in a game world.</summary>
        public uint TotalInGame;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            TotalInSystem = reader.ReadUInt32();
            TotalInGame = reader.ReadUInt32();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(TotalInSystem);
            writer.Write(TotalInGame);
        }

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} StatusCode:{StatusCode} TotalInSystem:{TotalInSystem} TotalInGame:{TotalInGame}";
    }
}
