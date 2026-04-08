namespace CheckersEngine.Engine;

/// <summary>
/// Static position evaluator for Brazilian checkers.
/// </summary>
/// <remarks>
/// Score convention: always from <b>black's</b> perspective.
/// <list type="bullet">
///   <item>Positive score → black (engine) is winning.</item>
///   <item>Negative score → red (human) is winning.</item>
/// </list>
/// <b>Components (all additive):</b>
/// <list type="number">
///   <item>Material — pawn = 100, king = 400.</item>
///   <item>Piece-square tables — pawn advancement bonus + king centrality.</item>
///   <item>Mobility — number of legal moves (weighted higher in endgame).</item>
///   <item>Threats — penalizes pieces that are immediately capturable.</item>
///   <item>Backed pieces — bonus for pieces with a diagonal friendly neighbor.</item>
///   <item>Pre-promotion — bonus for pawns one row from the back rank.</item>
///   <item>Border penalty — pawns on the a/h file are less mobile (middlegame only).</item>
///   <item>King pursuit — in deep endgames, bonus for kings close to enemy pieces.</item>
/// </list>
/// </remarks>
public static class Evaluator
{
    /// <summary>Material value of a pawn (centipawns).</summary>
    public const int PawnVal = 100;

    /// <summary>Material value of a king (centipawns).</summary>
    public const int KingVal = 400;

    /// <summary>
    /// Score threshold for a win/loss — always exceeds any positional score.
    /// </summary>
    public const int WinBase = 100_000;

    /// <summary>Score for black winning at the given remaining depth (prefers faster mates).</summary>
    public static int WinScore(int depth)  =>  WinBase + depth * 10;

    /// <summary>Score for black losing at the given remaining depth (delays loss as long as possible).</summary>
    public static int LoseScore(int depth) => -(WinBase + depth * 10);

    // ─── Piece-square tables ─────────────────────────────────────────────────

    // King centrality bonus — higher in the center, lower on edges
    private static readonly int[,] KingCenter =
    {
        {1, 0, 1, 0, 1, 0, 1, 0},
        {0, 3, 0, 3, 0, 3, 0, 1},
        {1, 0, 5, 0, 5, 0, 3, 0},
        {0, 3, 0, 8, 0, 8, 0, 3},
        {1, 0, 8, 0, 8, 0, 5, 0},
        {0, 3, 0, 5, 0, 5, 0, 3},
        {1, 0, 3, 0, 3, 0, 3, 0},
        {0, 1, 0, 1, 0, 1, 0, 1},
    };

    // Black pawn advancement (y=0 → y=7 is promotion row)
    private static readonly int[,] PawnAdvBlack =
    {
        { 0,  0,  0,  0,  0,  0,  0,  0},
        { 0,  1,  0,  1,  0,  1,  0,  1},
        { 2,  0,  3,  0,  3,  0,  2,  0},
        { 0,  4,  0,  5,  0,  5,  0,  4},
        { 5,  0,  6,  0,  6,  0,  5,  0},
        { 0,  6,  0,  7,  0,  7,  0,  6},
        { 8,  0,  8,  0,  8,  0,  8,  0},
        { 0, 10,  0, 10,  0, 10,  0, 10},
    };

    // Red pawn advancement (y=7 → y=0 is promotion row) — mirror of the black table
    private static readonly int[,] PawnAdvRed =
    {
        { 0, 10,  0, 10,  0, 10,  0, 10},
        { 8,  0,  8,  0,  8,  0,  8,  0},
        { 0,  6,  0,  7,  0,  7,  0,  6},
        { 5,  0,  6,  0,  6,  0,  5,  0},
        { 0,  4,  0,  5,  0,  5,  0,  4},
        { 2,  0,  3,  0,  3,  0,  2,  0},
        { 0,  1,  0,  1,  0,  1,  0,  1},
        { 0,  0,  0,  0,  0,  0,  0,  0},
    };

    // ─── Main evaluation ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the static evaluation score for <paramref name="board"/> from black's perspective.
    /// </summary>
    public static int Evaluate(Board board)
    {
        var (bp, bk, rp, rk) = board.CountPieces();
        int total = bp + bk + rp + rk;
        bool endgame     = total <= 10;
        bool deepEndgame = total <= 6;

        // Material
        int score = (bp * PawnVal + bk * KingVal) - (rp * PawnVal + rk * KingVal);

        // Piece-square tables
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            switch (board.Get(x, y))
            {
                case Piece.BlackPawn: score += PawnAdvBlack[y, x]; break;
                case Piece.RedPawn:   score -= PawnAdvRed[y, x];   break;
                case Piece.BlackKing: score += KingCenter[y, x] * (endgame ? 4 : 2); break;
                case Piece.RedKing:   score -= KingCenter[y, x] * (endgame ? 4 : 2); break;
            }
        }

        // Mobility — number of legal moves available to each side
        int mobB = MoveGenerator.GetLegalMoves(board, true).Count;
        int mobR = MoveGenerator.GetLegalMoves(board, false).Count;
        int mobW = endgame ? 10 : 6;
        score += (mobB - mobR) * mobW;

        // Immobility detection (no moves = forced loss)
        if (mobB == 0) score -= 9000;
        if (mobR == 0) score += 9000;

        // Threats — pieces immediately capturable by the opponent
        int threatW = endgame ? 60 : 40;
        score -= CountThreats(board, true)  * threatW;
        score += CountThreats(board, false) * threatW;

        // Backed pieces — diagonal friendly neighbor provides defense
        score += CountBackedPieces(board, true)  * 5;
        score -= CountBackedPieces(board, false) * 5;

        // Pre-promotion bonus
        for (int x = 0; x < 8; x++)
        {
            if (board.Get(x, 6) == Piece.BlackPawn) score += 30;
            if (board.Get(x, 1) == Piece.RedPawn)   score -= 30;
        }

        // Border penalty (middlegame only) — edge pawns lack mobility
        if (!endgame)
            for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                if (x != 0 && x != 7) continue;
                var p = board.Get(x, y);
                if      (p == Piece.BlackPawn) score -= 5;
                else if (p == Piece.RedPawn)   score += 5;
            }

        // Deep-endgame king-pursuit: bonus for kings close to enemy pieces
        if (deepEndgame)
        {
            if (bk > 0 && (rp + rk) <= 3)
                score -= KingPursuitDistance(board, Piece.BlackKing, true);

            if (rk > 0 && (bp + bk) <= 3)
                score += KingPursuitDistance(board, Piece.RedKing, false);
        }

        return score;
    }

    /// <summary>
    /// Returns a simplified material-only score (no positional terms).
    /// Used by the draw advisor for fast position assessment.
    /// </summary>
    public static int MaterialScore(Board board)
    {
        int score = 0;
        foreach (var p in board.Cells)
            score += p switch
            {
                Piece.BlackPawn =>  PawnVal,
                Piece.BlackKing =>  KingVal,
                Piece.RedPawn   => -PawnVal,
                Piece.RedKing   => -KingVal,
                _               =>  0,
            };
        return score;
    }

    // ─── Internal helpers ─────────────────────────────────────────────────────

    private static int CountThreats(Board board, bool blackTurn)
    {
        var caps       = MoveGenerator.GetAllCaptures(board, !blackTurn);
        var threatened = new HashSet<int>(caps.Count);
        foreach (var mv in caps)
        {
            int dx    = Math.Sign(mv.Tx - mv.Fx);
            int dy    = Math.Sign(mv.Ty - mv.Fy);
            int steps = Math.Abs(mv.Tx - mv.Fx);
            for (int i = 1; i < steps; i++)
            {
                int cx = mv.Fx + dx * i, cy = mv.Fy + dy * i;
                if (PieceHelper.IsMine(board.Get(cx, cy), blackTurn))
                    threatened.Add(cy * 8 + cx);
            }
        }
        return threatened.Count;
    }

    private static int CountBackedPieces(Board board, bool blackTurn)
    {
        int count = 0;
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            if (!PieceHelper.IsMine(board.Get(x, y), blackTurn)) continue;
            foreach (var (dx, dy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
            {
                int bx = x + dx, by = y + dy;
                if ((uint)bx <= 7 && (uint)by <= 7 && PieceHelper.IsMine(board.Get(bx, by), blackTurn))
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    private static int KingPursuitDistance(Board board, Piece kingPiece, bool blackKing)
    {
        int totalDist  = 0;
        bool enemyBlack = !blackKing;
        for (int ey = 0; ey < 8; ey++)
        for (int ex = 0; ex < 8; ex++)
        {
            if (!PieceHelper.IsMine(board.Get(ex, ey), enemyBlack)) continue;
            int minDist = 99;
            for (int ky = 0; ky < 8; ky++)
            for (int kx = 0; kx < 8; kx++)
                if (board.Get(kx, ky) == kingPiece)
                    minDist = Math.Min(minDist, Math.Max(Math.Abs(kx - ex), Math.Abs(ky - ey)));
            if (minDist < 99) totalDist += minDist;
        }
        return totalDist * 8;
    }
}
