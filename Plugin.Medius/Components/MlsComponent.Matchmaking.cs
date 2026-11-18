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
/// Resistance: Fall of Man's client does move: MatchFindGame with no extra
/// data asks for the match server (Referral: this listener again), and
/// with its 11 bytes of extra data it asks for a match, which is
/// answered the same way once the pool fills.
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
                if (request.Extra.Length == 0)
                    ReferToMatchServer(connection, client, request);
                else
                    QueueFindGame(connection, client, request);
                return true;

            case MediusJoinLeastPopulatedChannelRequest request:
                SendLeastPopulatedChannel(connection, client, request);
                return true;

            case MediusMatchCloseLateJoinRequest request:
                if (client.Game is { IsMatchmade: true } game)
                    game.AcceptsLateJoin = false;
                connection.SendApp(new MediusMatchCloseLateJoinResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                return true;

            case MediusMatchUpdateRequest request:
                // What the client reports is not known yet; it only needs the answer (MediusMatchUpdateRequest).
                Log.Information("{Tag} {Client} match update in {Game}: {Unk28} {Unk2C} {Rest}", Tag, client,
                    client.Game?.ToString() ?? "no game", request.Unk28, request.Unk2C, Convert.ToHexString(request.Rest));
                connection.SendApp(new MediusMatchUpdateResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusSuccess });
                return true;

            default:
                return false;
        }
    }

    private static void SendSupersets(ScertConnection connection, MediusTitle title, MessageId messageId)
    {
        List<MediusSuperset> supersets = title.Matchmaking.Supersets;
        bool repeated = title.RtLayout.SupersetStatusCodeRepeated, fixedStrings = title.RtLayout.SupersetFixedStrings;
        if (supersets.Count == 0)
        {
            connection.SendApp(new MediusMatchListSupersetsResponse { MessageID = messageId, StatusCode = MediusCallbackStatus.MediusNoResult, EndOfList = true, StatusCodeRepeated = repeated, FixedStrings = fixedStrings });
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
                FixedStrings = fixedStrings,
            });
        }
    }

    /// <summary>
    /// Fall of Man's lobby room manager moving to a superset's match server (0x334cec). It wants
    /// Referral and nothing else: any other answer is retried at once, for as long as the screen is
    /// open (0x3352e8) [C; live 2026-11-06]. The match server is this listener.
    /// </summary>
    private void ReferToMatchServer(ScertConnection connection, MediusClient client, MediusMatchFindGameRequest request)
    {
        if (client.Title.Matchmaking.Supersets.All(s => s.Id != request.SupersetID))
        {
            Log.Warning("{Tag} {Client} asked for the match server of unknown superset {Superset}", Tag, client, request.SupersetID);
            connection.SendApp(new MediusMatchFindGameResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusMatchServerNotFound, Unk1C = request.SupersetID });
            return;
        }
        Log.Information("{Tag} {Client} moving to the match server of superset {Superset}", Tag, client, request.SupersetID);
        connection.SendApp(new MediusMatchFindGameResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusMatchTypeReferral,
            Unk1C = request.SupersetID,
            Unk20 = request.SupersetID,
            ConnectInfo = LobbyConnectInfo(client, (int)request.SupersetID),
        });
    }

    /// <summary>The way back from the match server: the lobby channel with the fewest players.</summary>
    private void SendLeastPopulatedChannel(ScertConnection connection, MediusClient client, MediusJoinLeastPopulatedChannelRequest request)
    {
        MediusChannel channel = Context.Lobby.Channels(client.Title).MinBy(c => c.PlayerCount) ?? Context.Lobby.DefaultChannel(client.Title);
        connection.SendApp(new MediusJoinLeastPopulatedChannelResponse
        {
            MessageID = request.MessageID,
            StatusCode = MediusCallbackStatus.MediusSuccess,
            ConnectInfo = LobbyConnectInfo(client, channel.Id),
        });
    }

    private void QueueFindGame(ScertConnection connection, MediusClient client, MediusMatchFindGameRequest request)
    {
        MediusMatchmakingSettings settings = client.Title.Matchmaking;
        if (settings.Supersets.All(s => s.Id != request.SupersetID))
        {
            Log.Warning("{Tag} {Client} asked for a match in unknown superset {Superset}", Tag, client, request.SupersetID);
            connection.SendApp(new MediusMatchFindGameResponse { MessageID = request.MessageID, StatusCode = MediusCallbackStatus.MediusMatchServerNotFound, Unk1C = request.SupersetID });
            return;
        }

        MediusMatchEntry entry = Context.Lobby.Matchmaker.Enqueue(client, request);
        MediusMatchPool pool = settings.PoolFor(entry.SupersetId, entry.Key);
        Log.Information("{Tag} {Client} looking for a match: party of {Size} in superset {Superset} ({Pool}: {Min}-{Max} players), extra {Extra}",
            Tag, client, entry.Size, entry.SupersetId, pool.Name, pool.MinPlayers, pool.MaxPlayers, Convert.ToHexString(request.Extra));
        RunMatchmaking(client.Title);
    }

    /// <summary>Answers the match requests that have waited long enough; the client asks again (see <see cref="MediusMatchmakingSettings.FindGameWaitSeconds"/>).</summary>
    internal void ExpireFindGames()
    {
        foreach (MediusMatchEntry entry in Context.Lobby.Matchmaker.ExpireFindGames(DateTime.UtcNow))
        {
            Log.Information("{Tag} {Client}: no match in superset {Superset} yet", Tag, entry.Requester, entry.SupersetId);
            entry.Requester.Connection?.SendApp(new MediusMatchFindGameResponse { MessageID = entry.MessageID, StatusCode = MediusCallbackStatus.MediusNoResult, Unk1C = entry.SupersetId });
        }
    }

    private void QueueParty(ScertConnection connection, MediusClient client, MediusMatchPartyRequest request)
    {
        if (client.Title.RtLayout.MatchMessages306)
        {
            request.ReadAs306();
            Log.Debug("{Tag} {Client} MatchParty (3.06): {Request}", Tag, client, request);
        }
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
        connection.SendApp(new MediusMatchPartyResponse { MessageID = request.MessageID, StatusCode = (MediusCallbackStatus)settings.PartyQueuedStatus });
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
                bool layout306 = title.RtLayout.MatchMessages306;
                MediusMatchGameInfo gameInfo = new()
                {
                    MediusWorldID = match.Game.Id,
                    MatchName = match.Game.Name,
                    AddressList = GameAddressList(title, matchReply: true),
                    GameOptions = options,
                    Teams = layout306 ? [] : match.RosterFor(party, title.Matchmaking),
                    Layout306 = layout306,
                    ApplicationID = (uint)match.Game.AppId,
                    GameStats = match.Game.Stats,
                    GameFields = layout306 ? GameFields306(match.Game) : new uint[15],
                    GamePassword = match.Game.Password,
                    GameHostType = (uint)match.Game.HostType,
                };
                party.Requester.Connection?.SendApp(party.FindGame
                    ? new MediusMatchFindGameResponse { MessageID = party.MessageID, StatusCode = MediusCallbackStatus.MediusJoinAssignedGame, Unk1C = party.SupersetId, GameInfo = gameInfo }
                    : new MediusMatchFoundResponse
                    {
                        MessageID = party.MessageID,
                        Unk15 = title.Matchmaking.FoundKeyOf(party.Attributes),
                        StatusCode = MediusCallbackStatus.MediusJoinAssignedGame,
                        GameInfo = gameInfo,
                        Layout306 = layout306,
                    });
            }
        }
    }

    /// <summary>
    /// The 15 u32 of the 3.06 game info (+0x14c..+0x184) in Horizon's MediusGameInfo order
    /// [guess]: MinPlayers, MaxPlayers, GameLevel, PlayerSkillLevel, PlayerCount, RulesSet,
    /// GenericField1-8, WorldStatus.
    /// </summary>
    private static uint[] GameFields306(MediusGame game) =>
    [
        (uint)game.MinPlayers, (uint)game.MaxPlayers, (uint)game.GameLevel, (uint)game.PlayerSkillLevel,
        (uint)game.PlayerCount, (uint)game.RulesSet, .. game.GenericFields.Select(f => (uint)f), (uint)game.Status,
    ];
}
