using Plugin.Medius.Lobby;
using Plugin.Medius.RT.Common;
using Plugin.Medius.RT.Models;
using Plugin.Medius.Transport;
using ServerShared.Server;
using System.Collections.Concurrent;
using System.Net;

namespace Plugin.Medius.Components;

/// <summary>
/// The Distributed Message Engine: the stock Medius game server. Players of
/// one game world connect to it with the access key their MLS join gave
/// them, and it relays what they send each other (APP_BROADCAST,
/// APP_SINGLE, APP_LIST) over TCP or the aux UDP channel, announcing who
/// comes and goes. What the messages mean is up to the game.
/// </summary>
/// <remarks>
/// The connect sequence beyond the base RT handshake (version &gt; 108):
/// <code>
/// CLIENT_CONNECT_TCP_AUX_UDP (world id, access key) -> SERVER_CONNECT_REQUIRE
/// CLIENT_CONNECT_READY_REQUIRE -> SERVER_CONNECT_ACCEPT_TCP (player index, scert id)
/// CLIENT_CONNECT_READY_TCP     -> SERVER_STARTUP_INFO_NOTIFY, SERVER_INFO_AUX_UDP (UDP address)
///   udp: CLIENT_CONNECT_AUX_UDP -> SERVER_CONNECT_ACCEPT_AUX_UDP
/// CLIENT_CONNECT_READY_AUX_UDP -> SERVER_CONNECT_COMPLETE, DMEServerVersion; others get CONNECT_NOTIFY
/// </code>
/// A PS2 client at 108 gets the key, accept and UDP address straight away.
/// Ported from Horizon's Server.Dme (MIT), which one DME process per world
/// group and a UDP port per player; here every world shares one TCP and one
/// UDP listener, and UDP peers are told apart by source endpoint.
/// </remarks>
public sealed class DmeComponent(MediusContext context) : MediusComponent(context)
{
    private const string ServerVersion = "2.10.0009";

    private readonly ConcurrentDictionary<EndPoint, DmePlayer> _udpPeers = new();

    public override string Name => MediusComponentNames.Dme;

    private static DmePlayer? SeatOf(ScertConnection connection) => connection.ComponentState as DmePlayer;

    /// <summary>A plain TCP connect (no aux UDP): the same seat lookup, then the base RT sequence.</summary>
    protected override bool AcceptConnect(ScertConnection connection, RT_MSG_CLIENT_CONNECT_TCP connect) =>
        BindTitle(connection, connect.AppId) && TakeSeat(connection, connect.AccessToken, connect.TargetWorldId, auxUdp: false);

    protected override bool HandleOther(ScertConnection connection, BaseScertMessage message)
    {
        if (message is RT_MSG_CLIENT_CONNECT_TCP_AUX_UDP connect)
        {
            if (!BindTitle(connection, connect.AppId) || !TakeSeat(connection, connect.AccessToken, connect.ARG1, auxUdp: true))
            {
                connection.Disconnect();
                return true;
            }
            if (connection.MediusVersion > 108)
            {
                connection.Send(new RT_MSG_SERVER_CONNECT_REQUIRE { MaxPacketSize = Constants.MEDIUS_MESSAGE_MAXLEN, MaxUdpPacketSize = Constants.MEDIUS_UDP_MESSAGE_MAXLEN });
            }
            else
            {
                SendCryptKeyGame(connection);
                SendAccept(connection);
                SendUdpInfo(connection);
            }
            return true;
        }

        if (SeatOf(connection) is not { } seat)
        {
            Log.Warning("{Tag} {Connection}: {Message} before the DME connect", Tag, connection, message.Id);
            return true;
        }

        switch (message)
        {
            case RT_MSG_CLIENT_CONNECT_READY_AUX_UDP:
                Complete(connection, seat);
                return true;

            case RT_MSG_CLIENT_SET_RECV_FLAG flag:
                seat.RecvFlag = flag.Flag;
                return true;

            // Relayed messages go out as they arrive, so there is nothing to aggregate.
            case RT_MSG_CLIENT_SET_AGG_TIME:
            case RT_MSG_CLIENT_FLUSH_SINGLE:
                return true;

            case RT_MSG_CLIENT_TIMEBASE_QUERY query:
                connection.Send(new RT_MSG_SERVER_TIMEBASE_QUERY_NOTIFY { ClientTime = query.Timestamp, ServerTime = (uint)seat.Game.Clock.ElapsedMilliseconds });
                return true;

            default:
                return Relay(seat, message, udp: false);
        }
    }

    protected override void SendAccept(ScertConnection connection)
    {
        if (SeatOf(connection) is not { } seat)
        {
            base.SendAccept(connection);
            return;
        }
        connection.Send(new RT_MSG_SERVER_CONNECT_ACCEPT_TCP
        {
            PlayerId = (ushort)seat.Index,
            ScertId = seat.ScertId,
            PlayerCount = (ushort)seat.Game.Players.Length,
            IP = connection.Remote?.Address ?? IPAddress.Any,
        });
    }

    protected override void ConnectReady(ScertConnection connection, RT_MSG_CLIENT_CONNECT_READY_TCP ready)
    {
        if (SeatOf(connection) is not { } seat)
            return;
        seat.RecvFlag = ready.RecvFlag;
        if (!seat.AuxUdp)
        {
            Complete(connection, seat);
            return;
        }
        connection.Send(new RT_MSG_SERVER_STARTUP_INFO_NOTIFY
        {
            GameHostType = (byte)MGCL_GAME_HOST_TYPE.MGCLGameHostClientServerAuxUDP,
            Timestamp = (uint)seat.Game.Clock.ElapsedMilliseconds,
        });
        SendUdpInfo(connection);
    }

    internal override void Disconnected(ScertConnection connection)
    {
        if (SeatOf(connection) is not { } seat || seat.Connection != connection)
            return;
        connection.ComponentState = null;
        seat.Connection = null;
        if (seat.Udp is { } udp)
            _udpPeers.TryRemove(udp.Remote, out _);

        if (seat.Joined)
        {
            seat.Joined = false;
            foreach (DmePlayer other in seat.Game.JoinedPlayers.Where(p => p != seat && p.Receives(RT_RECV_FLAG.RECV_NOTIFICATION)))
                other.Send(new RT_MSG_SERVER_DISCONNECT_NOTIFY
                {
                    PlayerIndex = (short)seat.Index,
                    ScertId = (short)seat.ScertId,
                    IP = (seat.Udp?.Remote as IPEndPoint)?.Address ?? IPAddress.Any,
                });
        }
        Context.Lobby.LeaveGame(seat);
        Log.Information("{Tag} {Seat} left {Game} ({Count} left)", Tag, seat, seat.Game, seat.Game.PlayerCount);
    }

    /// <summary>
    /// One datagram on the DME's UDP listener. A peer is unknown until its
    /// CLIENT_CONNECT_AUX_UDP names the world and seat its TCP connection
    /// holds; after that its source endpoint identifies it.
    /// </summary>
    internal void HandleDatagram(CoreUdpSession session, ReadOnlySpan<byte> datagram)
    {
        if (_udpPeers.TryGetValue(session.EndPoint, out DmePlayer? seat) && seat.Connection is { } connection)
        {
            foreach (BaseScertMessage message in ScertDatagramPeer.Parse(datagram, connection.MediusVersion, connection.Cipher))
            {
                if (Context.Settings.LogMessages)
                    Log.Debug("{Tag} {Seat} <-udp {Message}", Tag, seat, message);
                switch (message)
                {
                    case RT_MSG_CLIENT_CONNECT_AUX_UDP:
                        AcceptUdp(session, seat);
                        break;
                    case RT_MSG_CLIENT_ECHO echo:
                        seat.Udp?.Send(new RT_MSG_CLIENT_ECHO { Value = echo.Value });
                        break;
                    case RT_MSG_SERVER_ECHO:
                    case RT_MSG_CLIENT_FLUSH_SINGLE:
                    case RT_MSG_CLIENT_DISCONNECT:
                    case RT_MSG_CLIENT_DISCONNECT_WITH_REASON:
                        break;
                    default:
                        if (!Relay(seat, message, udp: true))
                            Log.Warning("{Tag} {Seat}: unhandled UDP message {Message}", Tag, seat, message);
                        break;
                }
            }
            return;
        }

        // Unknown sender: only a connect is understood, and its layout depends
        // on the library version, which is not known until the seat is found.
        foreach (int version in new[] { 113, 108 })
        {
            // Read with the wrong layout, the ids come out as nonsense and match no seat.
            if (ScertDatagramPeer.Parse(datagram, version, null).OfType<RT_MSG_CLIENT_CONNECT_AUX_UDP>().FirstOrDefault() is not { } connect)
                continue;

            DmePlayer? candidate = Context.Lobby.GameByWorldId((int)connect.WorldId)?.Player(connect.PlayerId);
            if (candidate?.Connection is not { } owner || (owner.MediusVersion > 108) != (version > 108) ||
                (version > 108 && connect.ScertId != candidate.ScertId))
                continue;

            if (candidate.Udp is { } old)
                _udpPeers.TryRemove(old.Remote, out _);
            _udpPeers[session.EndPoint] = candidate;
            AcceptUdp(session, candidate);
            return;
        }
        Log.Debug("{Tag} {Remote}: UDP from an unknown peer dropped", Tag, session.EndPoint);
    }

    private void AcceptUdp(CoreUdpSession session, DmePlayer seat)
    {
        ScertConnection connection = seat.Connection!;
        seat.Udp = new ScertDatagramPeer(session.Server, session.EndPoint, connection.MediusVersion);
        seat.Udp.Send(new RT_MSG_SERVER_CONNECT_ACCEPT_AUX_UDP
        {
            PlayerId = (ushort)seat.Index,
            ScertId = seat.ScertId,
            PlayerCount = (ushort)seat.Game.Players.Length,
            EndPoint = session.EndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.Any, 0),
        });
        Log.Information("{Tag} {Seat}: aux UDP from {Remote}", Tag, seat, session.EndPoint);
    }

    /// <summary>Finds the seat the access key was issued for and ties the connection to it.</summary>
    private bool TakeSeat(ScertConnection connection, string? accessKey, uint worldId, bool auxUdp)
    {
        DmePlayer? seat = Context.Lobby.SeatByAccessKey(accessKey);
        if (seat is null || seat.Game.Id != worldId || seat.Game.Title != connection.Title)
        {
            Log.Warning("{Tag} {Remote}: no seat in world {World} for access key {Key}; the client must join the game at the MLS first",
                Tag, connection.Remote, worldId, accessKey);
            return false;
        }
        seat.Connection?.Disconnect();
        seat.Connection = connection;
        seat.AuxUdp = auxUdp;
        connection.Client = seat.Client;
        connection.ComponentState = seat;
        Log.Information("{Tag} {Seat} connecting to {Game}", Tag, seat, seat.Game);
        return true;
    }

    private void SendUdpInfo(ScertConnection connection) =>
        connection.Send(new RT_MSG_SERVER_INFO_AUX_UDP
        {
            Ip = Context.PublicAddress,
            Port = (ushort)Context.Settings.PortOf(MediusComponentNames.Dme, udp: true),
        });

    /// <summary>The player is in: count them, tell them so, and tell everyone else.</summary>
    private void Complete(ScertConnection connection, DmePlayer seat)
    {
        if (seat.Joined)
            return;
        seat.Joined = true;
        seat.Game.MarkStarted();

        connection.Send(new RT_MSG_SERVER_CONNECT_COMPLETE { ARG1 = (ushort)seat.Game.PlayerCount });
        if (connection.MediusVersion > 108)
            connection.SendApp(new DMEServerVersion { Version = ServerVersion });

        foreach (DmePlayer other in seat.Game.JoinedPlayers.Where(p => p != seat && p.Receives(RT_RECV_FLAG.RECV_NOTIFICATION)))
            other.Send(new RT_MSG_SERVER_CONNECT_NOTIFY
            {
                PlayerIndex = (short)seat.Index,
                ScertId = (short)seat.ScertId,
                IP = (seat.Udp?.Remote as IPEndPoint)?.Address ?? connection.Remote?.Address ?? IPAddress.Any,
            });
        Log.Information("{Tag} {Seat} joined {Game} ({Count} in)", Tag, seat, seat.Game, seat.Game.PlayerCount);
    }

    /// <summary>
    /// Relays an app message to its targets in the sender's world, as an
    /// APP_SINGLE naming the sender, over the channel it came in on.
    /// Returns false for anything that is not an app message.
    /// </summary>
    private static bool Relay(DmePlayer source, BaseScertMessage message, bool udp)
    {
        IEnumerable<DmePlayer> targets;
        byte[] payload;
        RT_RECV_FLAG flag;
        switch (message)
        {
            case RT_MSG_CLIENT_APP_BROADCAST broadcast:
                targets = source.Game.JoinedPlayers.Where(p => p != source);
                payload = broadcast.Payload;
                flag = RT_RECV_FLAG.RECV_BROADCAST;
                break;
            case RT_MSG_CLIENT_APP_SINGLE single:
                targets = source.Game.Player(single.TargetOrSource) is { Joined: true } target ? [target] : [];
                payload = single.Payload;
                flag = RT_RECV_FLAG.RECV_SINGLE;
                break;
            case RT_MSG_CLIENT_APP_LIST list:
                targets = list.Targets.Select(source.Game.Player).OfType<DmePlayer>().Where(p => p.Joined);
                payload = list.Payload;
                flag = RT_RECV_FLAG.RECV_LIST;
                break;
            default:
                return false;
        }

        RT_MSG_CLIENT_APP_SINGLE relayed = new() { TargetOrSource = (short)source.Index, Payload = payload ?? [] };
        foreach (DmePlayer target in targets.Where(t => t.Receives(flag)))
            target.Send(relayed, udp);
        return true;
    }
}
