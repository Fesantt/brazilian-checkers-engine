namespace CheckersEngine;

using CheckersEngine.Engine;

/// <summary>
/// Main entry point for the Brazilian Checkers (Damas Brasileiras) engine library.
/// </summary>
/// <remarks>
/// <b>Quick start:</b>
/// <code>
/// // 1. Create an engine (uses default 8 s budget)
/// var engine = new BrazilianCheckersEngine();
///
/// // 2. Get the starting position
/// var board = Board.StartingPosition();
///
/// // 3. Ask for the best move
/// Move? best = engine.FindBestMove(board);
/// if (best.HasValue)
///     Console.WriteLine($"Best move: {best.Value}");
///
/// // 4. Draw logic
/// var memory = new GameMemory();
/// DrawDecision d = engine.ShouldAcceptDraw(board, memory);
/// Console.WriteLine($"Accept draw? {d.Accept} — {d.Reason}");
/// </code>
///
/// <b>Custom configuration:</b>
/// <code>
/// var cfg = EngineConfig.Strong with { UseOpeningBook = false };
/// var engine = new BrazilianCheckersEngine(cfg);
/// </code>
///
/// <b>Thread safety:</b>
/// A single <see cref="BrazilianCheckersEngine"/> instance is <b>not</b> thread-safe.
/// Create one instance per concurrent game.
/// </remarks>
public sealed class BrazilianCheckersEngine
{
    private readonly EngineConfig _cfg;
    private readonly Search       _search;

    /// <summary>The configuration this engine instance was created with.</summary>
    public EngineConfig Config => _cfg;

    /// <summary>
    /// Creates an engine with the given configuration.
    /// </summary>
    /// <param name="config">
    /// Engine configuration. If <c>null</c>, <see cref="EngineConfig.Default"/> is used.
    /// </param>
    public BrazilianCheckersEngine(EngineConfig? config = null)
    {
        _cfg    = config ?? EngineConfig.Default;
        _search = new Search(_cfg);
    }

    // ─── Move search ─────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the best move for black (the engine side).
    /// Runs iterative deepening until the time budget is exhausted.
    /// </summary>
    /// <param name="board">Current board position.</param>
    /// <param name="activePieceX">
    /// Column of the piece currently in a chain capture, or <c>null</c> for a normal turn.
    /// </param>
    /// <param name="activePieceY">Row of the active piece (see <paramref name="activePieceX"/>).</param>
    /// <param name="thinkingMs">
    /// Override the think time in milliseconds. Pass 0 to use <see cref="EngineConfig.ThinkingMs"/>.
    /// </param>
    /// <returns>The best legal <see cref="Move"/>, or <c>null</c> if no moves are available (game over).</returns>
    public Move? FindBestMove(
        Board board,
        int?  activePieceX = null,
        int?  activePieceY = null,
        int   thinkingMs   = 0)
    {
        return _search.FindBestMove(board, activePieceX, activePieceY, thinkingMs);
    }

    // ─── Draw logic ───────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates whether the engine should accept a draw offer from the human.
    /// </summary>
    /// <param name="board">Current board state.</param>
    /// <param name="memory">
    /// Game memory instance. Pass the same object used throughout the game so
    /// repetition counts are accurate.
    /// </param>
    /// <returns>A <see cref="DrawDecision"/> with the recommendation and reason.</returns>
    public DrawDecision ShouldAcceptDraw(Board board, GameMemory memory) =>
        DrawAdvisor.ShouldAcceptDraw(board, memory, _cfg);

    /// <summary>
    /// Evaluates whether the engine should proactively offer a draw to the human.
    /// Call this <b>after</b> applying the engine's move, before returning the turn.
    /// </summary>
    /// <param name="board">Board state after the engine's move.</param>
    /// <param name="memory">Game memory instance.</param>
    /// <returns>A <see cref="DrawOffer"/> with the recommendation and optional message.</returns>
    public DrawOffer ShouldOfferDraw(Board board, GameMemory memory) =>
        DrawAdvisor.ShouldOfferDraw(board, memory, _cfg);

    // ─── Convenience helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Returns all legal moves for the given side. Enforces the maximum-capture rule.
    /// </summary>
    /// <param name="board">Current board state.</param>
    /// <param name="blackTurn"><c>true</c> for black (engine), <c>false</c> for red (human).</param>
    public static IReadOnlyList<Move> GetLegalMoves(Board board, bool blackTurn) =>
        MoveGenerator.GetLegalMoves(board, blackTurn);

    /// <summary>
    /// Applies a move to the board and returns the resulting position.
    /// </summary>
    public static Board ApplyMove(Board board, in Move mv) => board.Apply(mv);

    /// <summary>
    /// Returns <c>true</c> if the given side has no legal moves (the game is over).
    /// </summary>
    public static bool IsGameOver(Board board, bool blackTurn) =>
        !MoveGenerator.HasAnyMove(board, blackTurn);

    /// <summary>
    /// Returns the static evaluation score for a board position from black's perspective.
    /// Positive = black (engine) winning; negative = red (human) winning.
    /// Not a replacement for <see cref="FindBestMove"/> — use only for analysis.
    /// </summary>
    public static int Evaluate(Board board) => Evaluator.Evaluate(board);
}
