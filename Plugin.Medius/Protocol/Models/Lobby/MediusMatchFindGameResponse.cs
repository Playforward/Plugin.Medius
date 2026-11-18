using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;

namespace Plugin.Medius.RT.Models
{
    /// <summary>
    /// Answer to <see cref="MediusMatchFindGameRequest"/> (lobby-ext 0x60, patch codec 0x656e4c,
    /// struct 0xc0): MessageID, StatusCode (+0x18), a u32 (+0x1c), then a body chosen by StatusCode
    /// [C]. MediusMatchTypeReferral (8): a u32 (+0x20) and ConnectInfo (+0x24, 0x9c): the match server
    /// to move to. MediusJoinAssignedGame (6): <see cref="GameInfo"/> (world id +0x20, name[32], u32,
    /// address list, options at +0x78/+0x7c, roster at +0x80, read by 0x32debc, which needs both the
    /// options and the roster) [C]. MediusMatchTypeHostGame (7: two u32 and bytes) has no reader in the
    /// game and is kept raw. Other statuses carry no body. PSRewired answers Referral with the
    /// superset id in both u32 and as WorldID, pointing at a LAN address the client cannot reach [live].
    /// </summary>
    [MediusMessage(NetMessageTypes.MessageClassLobbyExt, MediusLobbyExtMessageIds.MatchFindGameResponse)]
    public class MediusMatchFindGameResponse : BaseLobbyExtMessage, IMediusResponse
    {
        public override byte PacketType => (byte)MediusLobbyExtMessageIds.MatchFindGameResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }
        public MediusCallbackStatus StatusCode;
        /// <summary>[guess] u32 at +0x1c; the superset id in PSRewired's answers [live].</summary>
        public uint Unk1C;
        /// <summary>[guess] Referral only: u32 at +0x20; the superset id in PSRewired's answers [live].</summary>
        public uint Unk20;
        /// <summary>Referral only: where to connect.</summary>
        public NetConnectionInfo ConnectInfo = new();
        /// <summary>JoinAssignedGame only: the game to join.</summary>
        public MediusMatchGameInfo GameInfo = new();
        /// <summary>The unparsed body of a HostGame answer.</summary>
        public byte[] RawBody = [];

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            base.Deserialize(reader);
            MessageID = reader.Read<MessageId>();
            StatusCode = reader.Read<MediusCallbackStatus>();
            Unk1C = reader.ReadUInt32();
            if (StatusCode == MediusCallbackStatus.MediusMatchTypeReferral)
            {
                Unk20 = reader.ReadUInt32();
                ConnectInfo = reader.Read<NetConnectionInfo>();
            }
            else if (StatusCode == MediusCallbackStatus.MediusJoinAssignedGame)
                GameInfo = reader.Read<MediusMatchGameInfo>();
            else if (StatusCode == MediusCallbackStatus.MediusMatchTypeHostGame)
                RawBody = reader.ReadRest();
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            base.Serialize(writer);
            writer.Write(MessageID ?? MessageId.Empty);
            writer.Write(StatusCode);
            writer.Write(Unk1C);
            if (StatusCode == MediusCallbackStatus.MediusMatchTypeReferral)
            {
                writer.Write(Unk20);
                writer.Write(ConnectInfo);
            }
            else if (StatusCode == MediusCallbackStatus.MediusJoinAssignedGame)
                writer.Write(GameInfo);
            else if (StatusCode == MediusCallbackStatus.MediusMatchTypeHostGame)
                writer.Write(RawBody);
        }

        public override string ToString() => base.ToString() +
            $" MessageID:{MessageID} StatusCode:{StatusCode} Unk1C:{Unk1C} Unk20:{Unk20} ConnectInfo:{ConnectInfo} GameInfo:{GameInfo} RawBody:{RawBody.Length}B";
    }
}
