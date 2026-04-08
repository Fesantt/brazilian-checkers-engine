namespace CheckersEngine;

using CheckersEngine.Engine;

/// <summary>
/// Tracks game history for draw detection and opponent behavior profiling.
/// </summary>
/// <remarks>
/// This class runs entirely in the calling thread and does not perform any minimax search.
/// It is designed to be used alongside <see cref="BrazilianCheckersEngine"/> for:
/// <list type="bullet">
///   <item>Position repetition detection (used by <see cref="DrawAdvisor"/>).</item>
///   <item>Collecting human move statistics across multiple games for behavior profiling.</item>
/// </list>
/// <b>Thread safety:</b> not thread-safe. Synchronize externally if calling from multiple threads.
/// </remarks>
public sealed class GameMemory
{
    private Dictionary<string, int> _positionCounts = new();
    private readonly List<HumanMove> _movesThisGame = [];
    private readonly List<HumanMove> _movesAllGames = [];

    // ─── Position repetition ──────────────────────────────────────────────────

    /// <summary>
    /// Records that the human moved from <paramref name="from"/> to <paramref name="to"/>
    /// on the given board <b>before</b> the move was applied.
    /// Call this after every human move to keep repetition counts up to date.
    /// </summary>
    public void RecordHumanMove(Board board, (int x, int y) from, (int x, int y) to)
    {
        var key = BoardKey(board);
        _positionCounts[key] = (_positionCounts.TryGetValue(key, out int c) ? c : 0) + 1;

        bool isCapture  = Math.Abs(to.x - from.x) > 1;
        bool isLeftSide = from.x <= 3;
        int  advance    = from.y - to.y;

        var mv = new HumanMove(from, to, isCapture, isLeftSide, advance);
        _movesThisGame.Add(mv);
        _movesAllGames.Add(mv);
    }

    /// <summary>
    /// Returns how many times the given board position has been reached this game.
    /// </summary>
    public int RepetitionCount(Board board) =>
        _positionCounts.TryGetValue(BoardKey(board), out int c) ? c : 0;

    /// <summary>
    /// Returns a repetition penalty score (centipawns) to discourage repeating positions.
    /// </summary>
    public int RepetitionPenalty(Board board)
    {
        int c = RepetitionCount(board);
        return c >= 2 ? 5000 : c == 1 ? 500 : 0;
    }

    // ─── Opponent profiling ───────────────────────────────────────────────────

    /// <summary>
    /// Returns statistical profile of the human player's move tendencies.
    /// Uses all-games data when ≥ 10 moves are available; otherwise current-game data.
    /// </summary>
    public HumanProfile GetHumanProfile()
    {
        var src = _movesAllGames.Count >= 10 ? _movesAllGames : _movesThisGame;
        int n   = src.Count;
        if (n == 0)
            return new HumanProfile(0, 0.5, 0.5, 0, 0);

        int captures   = 0;
        int leftMoves  = 0;
        int totalAdv   = 0;

        foreach (var mv in src)
        {
            if (mv.IsCapture)  captures++;
            if (mv.IsLeftSide) leftMoves++;
            totalAdv += mv.Advance;
        }

        return new HumanProfile(
            AggressionRate  : (double)captures  / n,
            LeftFlankRate   : (double)leftMoves  / n,
            RightFlankRate  : (double)(n - leftMoves) / n,
            AvgAdvance      : (double)totalAdv   / n,
            GamesLearned    : _movesAllGames.Count
        );
    }

    // ─── State management ─────────────────────────────────────────────────────

    /// <summary>
    /// Resets the current-game move history and position repetition counts.
    /// Call at the start of each new game. All-games statistics are preserved.
    /// </summary>
    public void ResetGame()
    {
        _positionCounts = new();
        _movesThisGame.Clear();
    }

    /// <summary>
    /// Resets all state including cross-game statistics.
    /// </summary>
    public void ResetAll()
    {
        _positionCounts = new();
        _movesThisGame.Clear();
        _movesAllGames.Clear();
    }

    // ─── Internal ─────────────────────────────────────────────────────────────

    private static string BoardKey(Board board)
    {
        var chars = new char[64 + 7]; // 64 cells + 7 '|' separators
        int pos   = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
                chars[pos++] = board.Get(x, y) switch
                {
                    Piece.BlackPawn => 'b',
                    Piece.BlackKing => 'B',
                    Piece.RedPawn   => 'r',
                    Piece.RedKing   => 'R',
                    _               => '.',
                };
            if (y < 7) chars[pos++] = '|';
        }
        return new string(chars, 0, pos);
    }

    // ─── Data types ───────────────────────────────────────────────────────────

    private readonly record struct HumanMove(
        (int x, int y) From,
        (int x, int y) To,
        bool IsCapture,
        bool IsLeftSide,
        int  Advance);
}

/// <summary>Statistical profile of the human player's move tendencies.</summary>
/// <param name="AggressionRate">Fraction of moves that were captures (0–1).</param>
/// <param name="LeftFlankRate">Fraction of moves originating from the left half of the board (x ≤ 3).</param>
/// <param name="RightFlankRate">Fraction of moves originating from the right half (x ≥ 4).</param>
/// <param name="AvgAdvance">Average row advancement per move (positive = moving toward back rank).</param>
/// <param name="GamesLearned">Total moves recorded across all games.</param>
public readonly record struct HumanProfile(
    double AggressionRate,
    double LeftFlankRate,
    double RightFlankRate,
    double AvgAdvance,
    int    GamesLearned);
