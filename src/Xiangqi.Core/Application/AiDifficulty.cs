namespace Xiangqi.Core.Application;

/// <summary>Stable player-facing difficulty. Engine-specific knobs are resolved by the
/// application profile and never interpreted by UI or domain rules.</summary>
public enum AiDifficultyLevel
{
    Beginner,
    Easy,
    Medium,
    Hard,
    Master,
    Custom
}

/// <summary>Search and deliberate weakening policy for a Xiangqi engine. A zero depth means
/// time-limited iterative deepening. CandidateRank is applied only after every candidate has
/// passed local legality validation; rank one always means the engine's best move.</summary>
public readonly record struct XiangqiDifficultyProfile(
    int MoveTimeMilliseconds,
    int MaxDepth,
    int VariationCount,
    int CandidateRank,
    int MaximumCentipawnLoss,
    bool UseDeterministicLegalMove = false);

/// <summary>Native Rapfi controls for one Gomoku search. Strength is a percentage in [1,100];
/// zero MaxDepth leaves iterative deepening bounded only by time.</summary>
public readonly record struct GomokuDifficultyProfile(
    int ThinkMilliseconds,
    int MaxDepth,
    int Strength,
    bool UseDeterministicLegalMove = false);

public static class AiDifficultyProfiles
{
    /// <summary>Fixed wall-clock budget per replay position. Replay analysis still uses the
    /// engine's strongest mode within this budget: one best line, no depth cap, and (for Gomoku)
    /// full native strength.</summary>
    public const int ReplayThinkMilliseconds = 500;
    /// <summary>Replay analysis is deliberately single-threaded so background sweeps do not
    /// contend with Godot rendering. This is an application policy, not a player setting.</summary>
    public const int ReplayThreads = 1;
    /// <summary>Fixed transposition-table budget for classic replay analysis.</summary>
    public const int ReplayHashMegabytes = 64;
    /// <summary>Fixed engine memory budget for Gomoku replay analysis.</summary>
    public const int ReplayMemoryMegabytes = 256;
    /// <summary>Delay between uncached replay positions. It yields CPU time to rendering and input;
    /// it is not included in the engine's per-position search budget.</summary>
    public const int ReplaySweepYieldMilliseconds = 50;

    public static XiangqiDifficultyProfile Xiangqi(AiDifficultyLevel level,
        int customMilliseconds = 1500, int customDepth = 0) => level switch
    {
        AiDifficultyLevel.Beginner => new(100, 1, 1, 1, 0, true),
        AiDifficultyLevel.Easy => new(100, 1, 8, 8, 5_000),
        AiDifficultyLevel.Medium => new(250, 3, 6, 5, 1_500),
        AiDifficultyLevel.Hard => new(800, 7, 2, 2, 200),
        AiDifficultyLevel.Master => new(3000, 0, 1, 1, 0),
        AiDifficultyLevel.Custom => new(Math.Clamp(customMilliseconds, 100, 120_000),
            Math.Clamp(customDepth, 0, 128), 1, 1, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };

    public static GomokuDifficultyProfile Gomoku(AiDifficultyLevel level,
        int customMilliseconds = 1500, int customDepth = 0) => level switch
    {
        AiDifficultyLevel.Beginner => new(100, 1, 1, true),
        AiDifficultyLevel.Easy => new(100, 1, 1),
        AiDifficultyLevel.Medium => new(250, 3, 15),
        AiDifficultyLevel.Hard => new(800, 8, 60),
        AiDifficultyLevel.Master => new(3000, 0, 100),
        AiDifficultyLevel.Custom => new(Math.Clamp(customMilliseconds, 100, 120_000),
            Math.Clamp(customDepth, 0, 128), 100),
        _ => throw new ArgumentOutOfRangeException(nameof(level))
    };
}
