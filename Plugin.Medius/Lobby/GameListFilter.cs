using Plugin.Medius.RT.Common;

namespace Plugin.Medius.Lobby;

/// <summary>
/// One condition a player put on their game list (MediusSetGameListFilter):
/// <c>baseline OP (field &amp; mask)</c>. A game is listed when it meets every
/// filter the player has set.
/// </summary>
public sealed class GameListFilter
{
    public uint Id { get; init; }
    public MediusGameListFilterField Field { get; init; }
    public MediusComparisonOperator Operator { get; init; }
    public int BaselineValue { get; init; }
    public uint Mask { get; init; } = uint.MaxValue;

    public bool IsMatch(MediusGame game)
    {
        long? value = Field switch
        {
            MediusGameListFilterField.MEDIUS_FILTER_PLAYER_COUNT => game.PlayerCount,
            MediusGameListFilterField.MEDIUS_FILTER_MIN_PLAYERS => game.MinPlayers,
            MediusGameListFilterField.MEDIUS_FILTER_MAX_PLAYERS => game.MaxPlayers,
            MediusGameListFilterField.MEDIUS_FILTER_GAME_LEVEL => game.GameLevel,
            MediusGameListFilterField.MEDIUS_FILTER_PLAYER_SKILL_LEVEL => game.PlayerSkillLevel,
            MediusGameListFilterField.MEDIUS_FILTER_RULES_SET => game.RulesSet,
            MediusGameListFilterField.MEDIUS_FILTER_LOBBY_WORLDID => game.Channel?.Id ?? 0,
            // Fields 1-3 predate the lobby world id filter; 4-8 were added after it.
            >= MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_1 and <= MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_3 =>
                game.GenericFields[Field - MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_1],
            >= MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_4 and <= MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_8 =>
                game.GenericFields[3 + (Field - MediusGameListFilterField.MEDIUS_FILTER_GENERIC_FIELD_4)],
            _ => null,
        };
        return value is { } v && Operator.Compare(BaselineValue, v & Mask);
    }
}
