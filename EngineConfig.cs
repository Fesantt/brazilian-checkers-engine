namespace CheckersEngine;

/// <summary>
/// Immutable configuration record for <see cref="BrazilianCheckersEngine"/>.
/// All properties have sensible defaults that match competition-strength play.
/// Use the static presets (<see cref="Fast"/>, <see cref="Default"/>, <see cref="Strong"/>)
/// or construct with <c>with</c> expressions for fine-grained control.
/// </summary>
/// <example>
/// <code>
/// // Use a preset
/// var engine = new BrazilianCheckersEngine(EngineConfig.Strong);
///
/// // Customize
/// var cfg = EngineConfig.Default with { ThinkingMs = 5_000, UseOpeningBook = false };
/// var engine = new BrazilianCheckersEngine(cfg);
/// </code>
/// </example>
public sealed record EngineConfig
{
    // ─── Time ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maximum time (milliseconds) the engine is allowed to think per move.
    /// Iterative deepening will stop as soon as this budget is exhausted.
    /// Default: 8 000 ms.
    /// </summary>
    public int ThinkingMs { get; init; } = 8_000;

    // ─── Depth ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimum depth the engine must complete before its result is accepted.
    /// If the deadline fires before <c>MinDepth</c> is reached, the best move
    /// found so far is still returned (never null if any move exists).
    /// Default: 9.
    /// </summary>
    public int MinDepth { get; init; } = 9;

    /// <summary>
    /// Hard cap on iterative-deepening depth (seldom reached in practice).
    /// Useful for testing at shallower depths.
    /// Default: 60.
    /// </summary>
    public int MaxDepth { get; init; } = 60;

    // ─── Transposition table ─────────────────────────────────────────────────

    /// <summary>
    /// Size of the transposition table as a power of 2.
    /// Actual entry count = 2^<c>TranspositionTableSizePow2</c>.
    /// Each entry is 32 bytes → total memory ≈ 2^N × 32 bytes.
    ///   • 21 → 2 097 152 entries ≈ 64 MB  (default)
    ///   • 20 → 1 048 576 entries ≈ 32 MB
    ///   • 22 → 4 194 304 entries ≈ 128 MB
    /// </summary>
    public int TranspositionTableSizePow2 { get; init; } = 21;

    // ─── Feature flags ────────────────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, the engine uses a hardcoded book of standard Brazilian
    /// checkers openings and responds instantly on book moves.
    /// Default: true.
    /// </summary>
    public bool UseOpeningBook { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, aspiration windows narrow the alpha-beta window around
    /// the previous iteration's score, enabling faster convergence.
    /// Default: true.
    /// </summary>
    public bool UseAspirationWindows { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, killer moves (2 per ply) are stored and tried before
    /// other quiet moves during move ordering, improving cut-off rates.
    /// Default: true.
    /// </summary>
    public bool UseKillerMoves { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, the history heuristic rewards quiet moves that caused
    /// beta cut-offs, improving ordering of non-capture moves.
    /// Default: true.
    /// </summary>
    public bool UseHistoryHeuristic { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, Late Move Reductions (LMR) reduce the search depth of
    /// late-ordered quiet moves, saving time without sacrificing much accuracy.
    /// Default: true.
    /// </summary>
    public bool UseLMR { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, a quiescence search is run at leaf nodes to resolve
    /// tactical captures before applying the static evaluation.
    /// Default: true.
    /// </summary>
    public bool UseQuiescence { get; init; } = true;

    // ─── LMR tuning ──────────────────────────────────────────────────────────

    /// <summary>
    /// Moves at or beyond this index (0-based, after ordering) are subject to
    /// LMR when <see cref="UseLMR"/> is true.
    /// Default: 4.
    /// </summary>
    public int LmrMinMoveIndex { get; init; } = 4;

    /// <summary>
    /// Minimum remaining depth for LMR to apply.
    /// Default: 3.
    /// </summary>
    public int LmrMinDepth { get; init; } = 3;

    /// <summary>
    /// Moves at or beyond this index are reduced by 2 instead of 1 (aggressive LMR).
    /// Default: 8.
    /// </summary>
    public int LmrAggressiveIndex { get; init; } = 8;

    // ─── Aspiration window ────────────────────────────────────────────────────

    /// <summary>
    /// Initial aspiration window half-width (centipawns).
    /// On a fail-high or fail-low, the window is widened up to a maximum.
    /// Default: 60.
    /// </summary>
    public int AspirationWindowInitial { get; init; } = 60;

    /// <summary>
    /// Maximum aspiration window half-width (centipawns) before falling back
    /// to a full-width search.
    /// Default: 2 000.
    /// </summary>
    public int AspirationWindowMax { get; init; } = 2_000;

    // ─── Draw advisor ─────────────────────────────────────────────────────────

    /// <summary>
    /// Material score threshold (centipawns) above which the engine refuses any
    /// draw offer — it considers itself winning.
    /// Default: 200 (2 pawns ahead).
    /// </summary>
    public int DrawRefuseAboveScore { get; init; } = 200;

    /// <summary>
    /// Material score threshold (centipawns) below which the engine accepts or
    /// offers a draw — it considers itself losing.
    /// Default: -200.
    /// </summary>
    public int DrawAcceptBelowScore { get; init; } = -200;

    /// <summary>
    /// Number of position repetitions that triggers draw consideration
    /// when the position is roughly equal.
    /// Default: 2.
    /// </summary>
    public int DrawRepetitionThreshold { get; init; } = 2;

    // ─── Presets ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fast preset: 2 s think time, shallower minimum depth.
    /// Suitable for rapid games or embedded environments.
    /// </summary>
    public static EngineConfig Fast => new()
    {
        ThinkingMs = 2_000,
        MinDepth   = 5,
        TranspositionTableSizePow2 = 20,
    };

    /// <summary>
    /// Default preset: 8 s think time, all features enabled.
    /// Matches the reference implementation in <c>services/checkers_bot</c>.
    /// </summary>
    public static EngineConfig Default => new();

    /// <summary>
    /// Strong preset: 15 s think time, deeper minimum depth.
    /// Recommended for analysis or correspondence play.
    /// </summary>
    public static EngineConfig Strong => new()
    {
        ThinkingMs = 15_000,
        MinDepth   = 12,
        TranspositionTableSizePow2 = 22,
    };

    /// <summary>
    /// Blitz preset: 1 s think time, aggressive LMR, small TT.
    /// Useful for stress-testing or rapid prototyping.
    /// </summary>
    public static EngineConfig Blitz => new()
    {
        ThinkingMs = 1_000,
        MinDepth   = 3,
        TranspositionTableSizePow2 = 19,
        LmrMinMoveIndex    = 2,
        LmrAggressiveIndex = 4,
    };
}
