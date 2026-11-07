using Plugin.Medius.Components;
using Plugin.Medius.Transport;

namespace Plugin.Medius.Extensions.Mag;

/// <summary>
/// A player the lobby wants to send into a battle: who they are, the
/// NetJoinGameRequest's transID, the lobby (MLS) connection it came in on,
/// their saved soldier if they have one, and the game type they queued for
/// (by name, <see cref="MagGameTypes"/>).
/// </summary>
/// <param name="GameTypes">
/// The game types the player queued for, by name (<see cref="MagGameTypes"/>):
/// one on 1.00, any number with 2.12's multiqueue. The player goes into the
/// first of them that has a seat for them.
/// </param>
public sealed record MagJoinRequest(int AccountId, string AccountName, int TransId, ScertConnection Lobby, MagCharacter? Character, IReadOnlyList<string> GameTypes);

/// <summary>
/// The game server's answer to a join: the one-time key the client presents
/// in its RT connect, the game it is going to, and its piuid. Everything
/// else NetJoinGameResponse carries (address, port, connection type) comes
/// from the Medius listener the game server's component is bound to.
/// </summary>
public sealed record MagJoinTicket(string AccessKey, uint GameId, uint Piuid);

/// <summary>
/// A MAG game (battle) server the lobby can hand players to. Retail MAG
/// queued players per game type and PMC until a game could be formed
/// (docs/ORIGINAL-GAME.md in Plugin.MagGameServer), so a join is answered
/// when the player has a seat, which may be at once or much later.
/// </summary>
public interface IMagGameServer
{
    /// <summary>The Medius listener component name players connect to (its port goes in NetJoinGameResponse).</summary>
    string ComponentName { get; }

    /// <summary>
    /// Puts the player in the queue for their game type. <paramref name="answer"/>
    /// is called once: with a ticket when they have a seat, or with null when
    /// no game can take them (the lobby then answers the join with a
    /// failure). It may be called before this returns, and from another thread.
    /// </summary>
    void Queue(MagJoinRequest request, Action<MagJoinTicket?> answer);

    /// <summary>Takes the account out of the queue without an answer (it gave up, or left); false if it wasn't waiting.</summary>
    bool Cancel(int accountId);

    /// <summary>The kits a new soldier of a PMC starts with in a client build (<see cref="MagCharacter.TakeDefaultLoadouts"/>).</summary>
    IReadOnlyList<MagLoadout> DefaultLoadouts(MagClientVersion version, int faction);
}

/// <summary>
/// What the lobby hands a game server when the title starts.
/// <paramref name="AwardExperience"/> adds battle experience to an account's
/// soldier (levelling it up), saves it and sends the client its new record
/// on its lobby connection, when given.
/// </summary>
/// <paramref name="RecordGame"/> adds a player's game to their soldier's
/// career stats. <paramref name="BonusRates"/> gives a PMC's experience
/// bonus rates in a game type (bonus stat name → rate, e.g. ShadowWarBonus
/// from the contracts it holds), and <paramref name="RecordWin"/> moves the
/// Shadow War for a game won (game type, the winning PMC, major victory).
public sealed record MagGameServerContext(MediusContext Medius, MagSettings Settings, Func<int, MagCharacter?> CharacterOf,
    Action<int, uint, Plugin.Medius.Transport.ScertConnection?>? AwardExperience = null, Action<MagGameRecord>? RecordGame = null,
    Func<string, int, IReadOnlyDictionary<string, float>>? BonusRates = null, Action<string, int, bool>? RecordWin = null);

/// <summary>
/// One player's game for their career: its game type (m_gameType, e.g.
/// GameType64), how it ended for them (an ExperienceGameResult name, null if
/// they left before the end) and the game's stats by CharacterStatType name
/// without the kCharStat prefix (EnemiesKilled, BattleExperience, ...).
/// </summary>
public sealed record MagGameRecord(int AccountId, string GameType, string? Result, IReadOnlyDictionary<string, long> Stats);

/// <summary>
/// Where a game server plugin plugs into the MAG lobby. The battle server
/// lives in its own plugin (Plugin.MagGameServer), which loads into the same
/// ServerApp process and registers a factory here before the Medius plugin
/// starts (lower plugin priority). Without one, the lobby answers every join
/// with a failure.
/// </summary>
public static class MagGameServerHost
{
    private static Func<MagGameServerContext, (IMagGameServer Server, IEnumerable<MediusComponent> Components)>? _factory;

    /// <summary>Registers the game server; the last registration wins.</summary>
    public static void Register(Func<MagGameServerContext, (IMagGameServer Server, IEnumerable<MediusComponent> Components)> factory)
    {
        _factory = factory;
        Log.Information("[medius/mag] game server registered");
    }

    public static void Unregister() => _factory = null;

    internal static (IMagGameServer? Server, IEnumerable<MediusComponent> Components) Create(MagGameServerContext context) =>
        _factory?.Invoke(context) is { } created ? (created.Server, created.Components) : (null, []);
}
