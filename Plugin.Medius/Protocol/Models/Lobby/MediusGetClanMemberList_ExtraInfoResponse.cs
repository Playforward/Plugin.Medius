using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Plugin.Medius.RT.Models
{
	[MediusMessage(NetMessageTypes.MessageClassLobby, MediusLobbyMessageIds.GetClanMemberList_ExtraInfoResponse)]
    public class MediusGetClanMemberList_ExtraInfoResponse : BaseLobbyMessage, IMediusResponse
    {
		public override byte PacketType => (byte)MediusLobbyMessageIds.GetClanMemberList_ExtraInfoResponse;

        public bool IsSuccess => StatusCode >= 0;

        public MessageId MessageID { get; set; }

        public MediusCallbackStatus StatusCode;
        public int AccountID;
        public string AccountName; // ACCOUNTNAME_MAXLEN
        public byte[] Stats; // ACCOUNTSTATS_MAXLEN
        public MediusPlayerOnlineState OnlineState;
        public int LadderStat;
        public uint LadderPosition;
        public uint TotalRankings;
        public bool EndOfList;

        /// <summary>
        /// Layout switch, not a field: whether LadderStat, LadderPosition and TotalRankings are on
        /// the wire (SCE-RT 3.06 dropped them, Killzone 3 codec 0xa34424) [C]. Set from the title's
        /// <see cref="ScertLayout.ClanExtraInfoLadderFields"/>.
        /// </summary>
        public bool LadderFields = true;

        public override void Deserialize(Plugin.Medius.RT.Shared.Stream.MessageReader reader)
        {
            // 
            base.Deserialize(reader);

            //
            MessageID = reader.Read<MessageId>();

            // 
            reader.ReadBytes(3);
            StatusCode = reader.Read<MediusCallbackStatus>();
            AccountID = reader.ReadInt32();
            AccountName = reader.ReadString(Constants.ACCOUNTNAME_MAXLEN);
            Stats = reader.ReadBytes(Constants.ACCOUNTSTATS_MAXLEN);
            OnlineState = reader.Read<MediusPlayerOnlineState>();
            if (LadderFields)
            {
                LadderStat = reader.ReadInt32();
                LadderPosition = reader.ReadUInt32();
                TotalRankings = reader.ReadUInt32();
            }
            EndOfList = reader.ReadBoolean();
            reader.ReadBytes(3);
        }

        public override void Serialize(Plugin.Medius.RT.Shared.Stream.MessageWriter writer)
        {
            // 
            base.Serialize(writer);

            //
            writer.Write(MessageID ?? MessageId.Empty);

            // 
            writer.Write(new byte[3]);
            writer.Write(StatusCode);
            writer.Write(AccountID);
            writer.Write(AccountName, Constants.ACCOUNTNAME_MAXLEN);
            writer.Write(Stats, Constants.ACCOUNTSTATS_MAXLEN);
            writer.Write(OnlineState);
            if (LadderFields)
            {
                writer.Write(LadderStat);
                writer.Write(LadderPosition);
                writer.Write(TotalRankings);
            }
            writer.Write(EndOfList);
            writer.Write(new byte[3]);
        }


        public override string ToString()
        {
            return base.ToString() + " " +
                $"MessageID:{MessageID} " +
             $"StatusCode:{StatusCode} " +
$"AccountID:{AccountID} " +
$"AccountName:{AccountName} " +
$"Stats:{Stats} " +
$"OnlineState:{OnlineState} " +
$"LadderStat:{LadderStat} " +
$"LadderPosition:{LadderPosition} " +
$"TotalRankings:{TotalRankings} " +
$"EndOfList:{EndOfList}";
        }
    }
}
