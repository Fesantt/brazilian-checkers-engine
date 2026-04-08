namespace CheckersEngine.Engine;

/// <summary>
/// Brazilian checkers move generator.
/// </summary>
/// <remarks>
/// <b>Rules implemented:</b>
/// <list type="bullet">
///   <item>Pawns move <b>forward only</b> (one diagonal step) but capture in <b>all 4 directions</b>.</item>
///   <item>Kings ("damas") slide any number of squares diagonally in any direction.</item>
///   <item>Captures are <b>mandatory</b>.</item>
///   <item>
///     The player <b>must</b> choose the capture sequence that takes the <b>most pieces</b>
///     (<i>regra do maior número</i> / maximum-capture rule).
///   </item>
///   <item>
///     During chain captures, a king cannot re-cross an already-captured square.
///     This is enforced with a <see cref="Piece.Sentinel"/> marker on captured squares.
///   </item>
///   <item>
///     A pawn reaching the promotion row mid-chain is NOT promoted until the
///     chain ends (<i>dama-voadora</i> rule).
///   </item>
/// </list>
/// </remarks>
public static class MoveGenerator
{
    private static readonly (int dx, int dy)[] AllDirs = [(1, 1), (1, -1), (-1, 1), (-1, -1)];

    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns all legal moves for the given side, enforcing the maximum-capture rule.
    /// If captures are available, only the capture sequences with the greatest chain
    /// length are returned. Falls back to normal (non-capture) moves if no captures exist.
    /// </summary>
    /// <param name="board">Current board state.</param>
    /// <param name="blackTurn"><c>true</c> → generate moves for black; <c>false</c> → for red.</param>
    public static List<Move> GetLegalMoves(Board board, bool blackTurn)
    {
        var caps = GetAllCaptures(board, blackTurn);
        if (caps.Count == 0)
            return GetNormalMoves(board, blackTurn);

        // Find maximum chain length across all possible first captures
        int maxDepth = 0;
        Span<int> depths = stackalloc int[caps.Count];

        for (int i = 0; i < caps.Count; i++)
        {
            var mv   = caps[i];
            var piece = board.Get(mv.Fx, mv.Fy);
            bool wouldPromote = (piece == Piece.BlackPawn && mv.Ty == 7) ||
                                (piece == Piece.RedPawn   && mv.Ty == 0);
            var nb  = board.Apply(mv, wouldPromote); // skip promotion mid-chain
            int dep = 1 + MaxCaptureDepth(nb, mv.Tx, mv.Ty, blackTurn);
            depths[i] = dep;
            if (dep > maxDepth) maxDepth = dep;
        }

        var result = new List<Move>(8);
        for (int i = 0; i < caps.Count; i++)
            if (depths[i] == maxDepth) result.Add(caps[i]);
        return result;
    }

    /// <summary>
    /// Returns <c>true</c> if the given side has at least one legal move.
    /// Slightly cheaper than calling <see cref="GetLegalMoves"/> when only
    /// the existence of a move matters.
    /// </summary>
    public static bool HasAnyMove(Board board, bool blackTurn) =>
        GetAllCaptures(board, blackTurn).Count > 0 || GetNormalMoves(board, blackTurn).Count > 0;

    /// <summary>
    /// Returns all single-step capture moves (no chaining) for the given side.
    /// Used internally for threat detection and quiescence search.
    /// </summary>
    public static List<Move> GetAllCaptures(Board board, bool blackTurn)
    {
        var result = new List<Move>(12);

        for (int fy = 0; fy < 8; fy++)
        for (int fx = 0; fx < 8; fx++)
        {
            var piece = board.Get(fx, fy);
            if (piece == Piece.Empty || !PieceHelper.IsMine(piece, blackTurn)) continue;

            if (PieceHelper.IsKing(piece))
                AddKingCaptures(board, fx, fy, blackTurn, result);
            else
                AddPawnCaptures(board, fx, fy, blackTurn, result);
        }

        return result;
    }

    // ─── Capture depth ───────────────────────────────────────────────────────

    /// <summary>
    /// Recursively computes the maximum number of <b>additional</b> captures
    /// reachable from (<paramref name="fx"/>, <paramref name="fy"/>) after an
    /// initial capture has already been applied to <paramref name="board"/>.
    /// </summary>
    /// <remarks>
    /// Kings use sentinel markers so they cannot re-traverse an already-captured diagonal.
    /// </remarks>
    public static int MaxCaptureDepth(Board board, int fx, int fy, bool blackTurn)
    {
        var piece = board.Get(fx, fy);
        if (piece == Piece.Empty) return 0;
        int best = 0;

        if (PieceHelper.IsKing(piece))
        {
            foreach (var (dx, dy) in AllDirs)
            {
                bool found = false;
                int ex = 0, ey = 0;
                for (int s = 1; s <= 7; s++)
                {
                    int nx = fx + dx * s, ny = fy + dy * s;
                    if ((uint)nx > 7 || (uint)ny > 7) break;
                    var p = board.Get(nx, ny);
                    if (p == Piece.Empty)
                    {
                        if (!found) continue;
                        var nb = board.Clone();
                        nb.Set(fx, fy, Piece.Empty);
                        nb.Set(ex, ey, Piece.Sentinel);
                        nb.Set(nx, ny, piece);
                        int depth = 1 + MaxCaptureDepth(nb, nx, ny, blackTurn);
                        if (depth > best) best = depth;
                    }
                    else if (p == Piece.Sentinel || PieceHelper.IsMine(p, blackTurn) || found) break;
                    else { found = true; ex = nx; ey = ny; }
                }
            }
        }
        else
        {
            foreach (var (dx, dy) in AllDirs)
            {
                int mx = fx + dx, my = fy + dy;
                int tx = fx + dx * 2, ty = fy + dy * 2;
                if ((uint)tx > 7 || (uint)ty > 7) continue;
                if (PieceHelper.IsEnemy(board.Get(mx, my), blackTurn) && board.Get(tx, ty) == Piece.Empty)
                {
                    var nb = board.Clone();
                    nb.Set(fx, fy, Piece.Empty);
                    nb.Set(mx, my, Piece.Empty);
                    nb.Set(tx, ty, piece);
                    int depth = 1 + MaxCaptureDepth(nb, tx, ty, blackTurn);
                    if (depth > best) best = depth;
                }
            }
        }

        return best;
    }

    // ─── Internal helpers ────────────────────────────────────────────────────

    private static void AddKingCaptures(Board board, int fx, int fy, bool blackTurn, List<Move> result)
    {
        foreach (var (dx, dy) in AllDirs)
        {
            bool enemyFound = false;
            for (int s = 1; s <= 7; s++)
            {
                int nx = fx + dx * s, ny = fy + dy * s;
                if ((uint)nx > 7 || (uint)ny > 7) break;
                var p = board.Get(nx, ny);
                if (p == Piece.Empty)
                {
                    if (enemyFound) result.Add(new Move(fx, fy, nx, ny, true));
                }
                else if (p == Piece.Sentinel || PieceHelper.IsMine(p, blackTurn) || enemyFound)
                    break;
                else
                    enemyFound = true;
            }
        }
    }

    private static void AddPawnCaptures(Board board, int fx, int fy, bool blackTurn, List<Move> result)
    {
        foreach (var (dx, dy) in AllDirs)
        {
            int mx = fx + dx, my = fy + dy;
            int tx = fx + dx * 2, ty = fy + dy * 2;
            if ((uint)tx > 7 || (uint)ty > 7) continue;
            if (PieceHelper.IsEnemy(board.Get(mx, my), blackTurn) && board.Get(tx, ty) == Piece.Empty)
                result.Add(new Move(fx, fy, tx, ty, true));
        }
    }

    private static List<Move> GetNormalMoves(Board board, bool blackTurn)
    {
        var result = new List<Move>(16);
        int dir = blackTurn ? 1 : -1;

        for (int fy = 0; fy < 8; fy++)
        for (int fx = 0; fx < 8; fx++)
        {
            var piece = board.Get(fx, fy);
            if (piece == Piece.Empty || !PieceHelper.IsMine(piece, blackTurn)) continue;

            if (PieceHelper.IsKing(piece))
            {
                foreach (var (dx, dy) in AllDirs)
                for (int s = 1; s <= 7; s++)
                {
                    int nx = fx + dx * s, ny = fy + dy * s;
                    if ((uint)nx > 7 || (uint)ny > 7) break;
                    if (board.Get(nx, ny) != Piece.Empty) break;
                    result.Add(new Move(fx, fy, nx, ny));
                }
            }
            else
            {
                foreach (int dx in (int[])[-1, 1])
                {
                    int nx = fx + dx, ny = fy + dir;
                    if ((uint)nx <= 7 && (uint)ny <= 7 && board.Get(nx, ny) == Piece.Empty)
                        result.Add(new Move(fx, fy, nx, ny));
                }
            }
        }

        return result;
    }
}
