using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Components;

/// <summary>
/// Matchmaking (the SCE-RT "MMS" messages), served on the MLS connection:
/// Resistance 2's client never moves to a separate match server (its
/// MLS-to-MMS reconnect, 0x2690c4, is a stub that always fails), so these
/// arrive here.
/// </summary>
/// <remarks>
/// MatchListSupersets -> one response per superset. MatchParty -> queued
/// (MatchPartyResponse), and when the pool fills, a game world is made and
/// every matched party's requester gets MatchFoundResponse
/// (MediusJoinAssignedGame) naming it; they then join it with
/// MediusJoinGame like any other game. See docs/MATCHMAKING.md.
/// </remarks>
public sealed partial class MlsComponent
{
    /// <summary>Handles a matchmaking request; false for anything else.</summary>
    private bool HandleMatchmaking(ScertConnection connection, MediusClient client, BaseMediusMessage message)
    {
        switch (message)
        {
            case MediusMatchListSupersetsRequest request:
                SendSupersets(connection, client.Title, request.MessageID);
                return true;

            case MediusMatchPartyRequest request:
                QueueParty(connection, client, request);
                return true;

            case MediusMatchFindGameRequest request:
                // Resistance: Fall of Man's quick match (lobby-ext 0x78). Its Referral answer needs a
                // matchmade world with the game's options, which is not built yet; NoResult [guess]
                // rather than leaving the client waiting.
                Log.Warning("{Tag} {Client} asked for a quick match in superset {Superset} (players {Players}); not served yet",
                    Tag, client, request.SupersetID, string.Join(",", request.AccountIDs));
                connection.SendApp(new MediusMatchFindGameResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, Unk1C = request.SupersetID });
                return true;

            case MediusMatchCloseLateJoinRequest request:
                if (client.Game is { IsMatchmade: true } game)
                    game.AcceptsLateJoin = false;
                connection.SendApp(new MediusMatchCloseLateJoinResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                return true;

            default:
                return false;
        }
    }

    private static void SendSupersets(ScertConnection connection, MediusTitle title, MessageId messageId)
    {
        List<MediusSuperset> supersets = title.Matchmaking.Supersets;
        bool repeated = title.RtLayout.SupersetStatusCodeRepeated;
        if (supersets.Count == 0)
        {
            connection.SendApp(new MediusMatchListSupersetsResponse { MessageID = messageId, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true, StatusCodeRepeated = repeated });
            return;
        }
        for (int i = 0; i < supersets.Count; i++)
        {
            connection.SendApp(new MediusMatchListSupersetsResponse
            {
                MessageID = messageId,
                StatusCode = MediusCallbackStatus.MediusSuccess,
                EndOfList = i == supersets.Count - 1,
                SupersetID = supersets[i].Id,
                SupersetName = supersets[i].Name,
                SupersetDescription = supersets[i].Description,
                SupersetExtraInfo = supersets[i].ExtraInfo,
                StatusCodeRepeated = repeated,
            });
        }
    }

    private void QueueParty(ScertConnection connection, MediusClient client, MediusMatchPartyRequest request)
    {
        MediusMatchmakingSettings settings = client.Title.Matchmaking;
        if (settings.Supersets.Count > 0 && settings.Supersets.All(s => s.Id != request.SupersetID))
        {
            Log.Warning("{Tag} {Client} asked for a match in unknown superset {Superset}", Tag, client, request.SupersetID);
            connection.SendApp(new MediusMatchPartyResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusMatchServerNotFound });
            return;
        }

        MediusMatchEntry entry = Context.Lobby.Matchmaker.Enqueue(client, request);
        MediusMatchPool pool = settings.PoolFor(entry.SupersetId, entry.Key);
        Log.Information("{Tag} {Client} queued a party of {Size} in superset {Superset} pool {Key} ({Pool}: {Min}-{Max} players)",
            Tag, client, entry.Size, entry.SupersetId, entry.Key, pool.Name, pool.MinPlayers, pool.MaxPlayers);
        connection.SendApp(new MediusMatchPartyResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
        RunMatchmaking(client.Title);
    }

    /// <summary>Makes the matches the queue allows and tells each matched party where to go.</summary>
    private void RunMatchmaking(MediusTitle title)
    {
        if (Context.Settings.PortOf(MediusComponentNames.Dme, udp: false) == 0)
        {
            Log.Error("{Tag} matchmaking needs a DME listener to put matched games on; add DME listeners to MediusConfig.json", Tag);
            return;
        }

        foreach (MediusMatch match in Context.Lobby.Matchmaker.TryMatch(title, Context.Lobby.Games(title), (pool, parties) =>
                     Context.Lobby.CreateGame(title, id => new MediusGame(id, title, pool, parties[0].Requester))))
        {
            Log.Information("{Tag} matched {Game} ({Pool} {Values}, up to {Max}, {Seated} sent there): {Parties}", Tag, match.Game, match.Pool.Name,
                string.Join(" ", match.Pool.Values.Select(v => $"{v.Key}={v.Value}")), match.Pool.MaxPlayers, match.Game.MatchTeamSizes.Sum(),
                string.Join(", ", match.Parties.Select(p => $"{p.Party.Requester} (party of {p.Party.Size}, team {p.Team})")));
            byte[] options = match.GameOptions();
            foreach ((MediusMatchEntry party, _) in match.Parties)
            {
                party.Requester.Connection?.SendApp(new MediusMatchFoundResponse
                {
                    MessageID = party.MessageID,
                    StatusCode = MediusCallbackStatus.MediusJoinAssignedGame,
                    GameInfo = new MediusMatchGameInfo
                    {
                        MediusWorldID = match.Game.Id,
                        MatchName = match.Game.Name,
                        AddressList = GameAddressList(title, matchReply: true),
                        GameOptions = options,
                        Teams = match.RosterFor(party, title.Matchmaking),
                    },
                });
            }
        }
    }
}
