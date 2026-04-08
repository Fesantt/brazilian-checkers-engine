namespace CheckersEngine.Engine.Tablebase;

/// <summary>Progress reported for each piece configuration during generation.</summary>
/// <param name="Config">Human-readable configuration label (e.g. <c>"2bp+1bk vs 3rp+0rk"</c>).</param>
/// <param name="Positions">Total position count for this configuration (both sides to move).</param>
/// <param name="Wins">Positions that are a Win for the side to move.</param>
/// <param name="Losses">Positions that are a Loss for the side to move.</param>
/// <param name="Draws">Positions that are a Draw.</param>
/// <param name="ElapsedMs">Wall-clock time for this configuration in milliseconds.</param>
public readonly record struct GenerationProgress(
    string Config,
    long   Positions,
    long   Wins,
    long   Losses,
    long   Draws,
    long   ElapsedMs);

/// <summary>
/// Retrograde-analysis tablebase generator for Brazilian checkers endgames.
/// </summary>
/// <remarks>
/// <b>Algorithm (per configuration):</b>
/// <list type="number">
///   <item><b>Phase 1 — Terminal and cross-config:</b> scan every position.
///     If no legal moves → Loss(0). For each capture move, look up the successor in the
///     already-generated sub-tablebase (one fewer piece). If any capture leads to an opponent
///     Loss → Win for the current side.</item>
///   <item><b>Phase 2 — BFS retrograde:</b> propagate Win/Loss backwards through non-capture
///     moves within the same configuration using reverse move generation.</item>
///   <item><b>Phase 3 — Draw:</b> any position still Unknown after convergence is a Draw.</item>
/// </list>
/// Configurations are generated in order of increasing piece count so sub-tablebase lookups
/// are always available.
/// </remarks>
internal static class TablebaseGenerator
{
    /// <summary>
    /// Generates all configurations with up to <paramref name="maxPieces"/> total pieces,
    /// writing results into <paramref name="tb"/>.
    /// </summary>
    public static void Generate(
        EndgameTablebase tb,
        int maxPieces,
        IProgress<GenerationProgress>? progress)
    {
        for (int n = 2; n <= maxPieces; n++)
        {
            for (int bp = 0; bp <= n; bp++)
            for (int bk = 0; bk <= n - bp; bk++)
            for (int rp = 0; rp <= n - bp - bk; rp++)
            {
                int rk = n - bp - bk - rp;
                if (bp + bk == 0 || rp + rk == 0) continue; // need ≥1 piece per side
                GenerateConfig(tb, bp, bk, rp, rk, progress);
            }
        }
    }

    // ─── Per-configuration generation ────────────────────────────────────────

    private static void GenerateConfig(
        EndgameTablebase tb,
        int bp, int bk, int rp, int rk,
        IProgress<GenerationProgress>? progress)
    {
        long size  = PositionIndex.ConfigSize(bp, bk, rp, rk);
        var  sw    = System.Diagnostics.Stopwatch.StartNew();

        // Per-position result tables (black-to-move and red-to-move)
        // Byte encoding: (outcome << 6) | min(dtm, 63)
        var tableB = new byte[size];
        var tableR = new byte[size];

        // Number of "neutral" moves remaining before a position can be declared a Loss.
        // A "neutral" move is: a non-capture move whose outcome is not yet known,
        // or a capture leading to a Draw/Unknown sub-tablebase result.
        // When this reaches 0 → all moves lead to opponent Win → position is Loss.
        var remB = new int[size];
        var remR = new int[size];

        // Tracks the running maximum DTM when accumulating "all moves are bad" evidence.
        // Stored separately to avoid polluting the result byte during BFS.
        var dtmTrackB = new int[size];
        var dtmTrackR = new int[size];

        var queue = new Queue<(long idx, bool blackSide)>();

        // ─── Phase 1 ─────────────────────────────────────────────────────────

        for (long idx = 0; idx < size; idx++)
        {
            var board = PositionIndex.Decode(idx, bp, bk, rp, rk);
            Phase1(board, idx, true,  bp, bk, rp, rk, tb, tableB, tableR, remB, remR, dtmTrackB, dtmTrackR, queue);
            Phase1(board, idx, false, bp, bk, rp, rk, tb, tableB, tableR, remB, remR, dtmTrackB, dtmTrackR, queue);
        }

        // ─── Phase 2 — BFS retrograde ─────────────────────────────────────────

        while (queue.Count > 0)
        {
            var (idx, blackSide) = queue.Dequeue();
            var result = TablebaseResult.Unpack((blackSide ? tableB : tableR)[idx]);

            // The PREVIOUS mover was !blackSide: find positions where !blackSide moved non-capture
            // to reach (idx, blackSide).
            var board = PositionIndex.Decode(idx, bp, bk, rp, rk);

            foreach (var pred in BackwardNonCapturePredecessors(board, !blackSide))
            {
                long predIdx = PositionIndex.Encode(pred, bp, bk, rp, rk);
                if (predIdx < 0) continue; // config mismatch — should not happen for non-captures

                // Predecessor had !blackSide to move → look up in opposite table
                bool predBlack = !blackSide;
                var  predTable = predBlack ? tableB : tableR;
                var  predRem   = predBlack ? remB   : remR;
                var  predDtm   = predBlack ? dtmTrackB : dtmTrackR;

                if (predTable[predIdx] != 0) continue; // already resolved

                if (result.Outcome == TablebaseOutcome.Loss)
                {
                    // Current position is a Loss for (blackSide → the side that moves now).
                    // The predecessor (!blackSide) moved here → it made a move where the opponent
                    // loses → predecessor is a Win.
                    int d = Math.Min(result.Dtm + 1, 63);
                    predTable[predIdx] = new TablebaseResult(TablebaseOutcome.Win, d).Pack();
                    queue.Enqueue((predIdx, predBlack));
                }
                else if (result.Outcome == TablebaseOutcome.Win)
                {
                    // Current position is a Win for (blackSide). Predecessor (!blackSide) moved
                    // into a position where the opponent will win → this move is bad for !blackSide.
                    int d = Math.Min(result.Dtm + 1, 63);
                    if (d > predDtm[predIdx]) predDtm[predIdx] = d;

                    if (--predRem[predIdx] == 0)
                    {
                        // All neutral moves exhausted → predecessor has no escape → Loss
                        predTable[predIdx] = new TablebaseResult(TablebaseOutcome.Loss, predDtm[predIdx]).Pack();
                        queue.Enqueue((predIdx, predBlack));
                    }
                }
            }
        }

        // ─── Phase 3 — Draw ────────────────────────────────────────────────────

        const byte DrawByte = (byte)(((byte)TablebaseOutcome.Draw << 6) | 0);
        for (long idx = 0; idx < size; idx++)
        {
            if (tableB[idx] == 0) tableB[idx] = DrawByte;
            if (tableR[idx] == 0) tableR[idx] = DrawByte;
        }

        tb.StoreConfig(bp, bk, rp, rk, tableB, tableR);

        // Report
        if (progress != null)
        {
            long wins = 0, losses = 0, draws = 0;
            string label = $"{bp}bp+{bk}bk vs {rp}rp+{rk}rk";
            for (long i = 0; i < size; i++)
            {
                switch ((TablebaseOutcome)(tableB[i] >> 6))
                {
                    case TablebaseOutcome.Win:  wins++;   break;
                    case TablebaseOutcome.Loss: losses++; break;
                    case TablebaseOutcome.Draw: draws++;  break;
                }
                switch ((TablebaseOutcome)(tableR[i] >> 6))
                {
                    case TablebaseOutcome.Win:  wins++;   break;
                    case TablebaseOutcome.Loss: losses++; break;
                    case TablebaseOutcome.Draw: draws++;  break;
                }
            }
            progress.Report(new GenerationProgress(label, size * 2, wins, losses, draws, sw.ElapsedMilliseconds));
        }
    }

    // ─── Phase 1 helper ───────────────────────────────────────────────────────

    private static void Phase1(
        Board board, long idx, bool blackSide,
        int bp, int bk, int rp, int rk,
        EndgameTablebase tb,
        byte[] tableB, byte[] tableR,
        int[] remB,    int[] remR,
        int[] dtmTrkB, int[] dtmTrkR,
        Queue<(long, bool)> queue)
    {
        var table    = blackSide ? tableB : tableR;
        var rem      = blackSide ? remB   : remR;
        var dtmTrack = blackSide ? dtmTrkB : dtmTrkR;

        var moves = MoveGenerator.GetLegalMoves(board, blackSide);

        if (moves.Count == 0)
        {
            table[idx] = new TablebaseResult(TablebaseOutcome.Loss, 0).Pack();
            queue.Enqueue((idx, blackSide));
            return;
        }

        int nonCaptures     = 0;
        int neutralCaptures = 0;
        bool isWin          = false;
        int  bestWinDtm     = int.MaxValue;
        int  worstBadDtm    = 0; // for all-bad-captures Loss case

        foreach (var mv in moves)
        {
            if (!mv.IsCapture)
            {
                nonCaptures++;
                continue;
            }

            // Look up capture successor in the sub-tablebase (fewer pieces)
            var  next   = board.Apply(mv);
            var  counts = CountPieces(next);
            var  sub    = tb.ProbeConfig(counts.bp, counts.bk, counts.rp, counts.rk, next, !blackSide);

            if (sub.Outcome == TablebaseOutcome.Loss)
            {
                // Opponent loses from successor → this capture wins
                if (!isWin || sub.Dtm + 1 < bestWinDtm)
                {
                    isWin       = true;
                    bestWinDtm  = sub.Dtm + 1;
                }
            }
            else if (sub.Outcome == TablebaseOutcome.Win)
            {
                // Opponent wins from successor → this capture is bad (not in neutral count)
                if (sub.Dtm + 1 > worstBadDtm) worstBadDtm = sub.Dtm + 1;
            }
            else
            {
                // Draw or Unknown → neutral (current side may draw via this capture)
                neutralCaptures++;
            }
        }

        if (isWin)
        {
            table[idx] = new TablebaseResult(TablebaseOutcome.Win, Math.Min(bestWinDtm, 63)).Pack();
            queue.Enqueue((idx, blackSide));
            return;
        }

        // remaining = moves not yet proven to lead to opponent Win
        rem[idx] = nonCaptures + neutralCaptures;

        if (rem[idx] == 0)
        {
            // All moves are bad captures (opponent always wins) → current position is Loss
            int dtm = Math.Min(worstBadDtm, 63); // losing side prolongs by taking slowest loss
            table[idx] = new TablebaseResult(TablebaseOutcome.Loss, dtm).Pack();
            queue.Enqueue((idx, blackSide));
        }
    }

    // ─── Backward non-capture move generation ─────────────────────────────────

    /// <summary>
    /// Generates all board states that could have transitioned into <paramref name="board"/>
    /// via a single non-capture move by <paramref name="prevMoverIsBlack"/>.
    /// </summary>
    private static IEnumerable<Board> BackwardNonCapturePredecessors(Board board, bool prevMoverIsBlack)
    {
        for (int sq = 0; sq < 32; sq++)
        {
            var (tx, ty) = PositionIndex.Squares[sq];
            var piece = board.Get(tx, ty);

            // Only consider pieces belonging to the previous mover
            if (prevMoverIsBlack && piece != Piece.BlackPawn && piece != Piece.BlackKing) continue;
            if (!prevMoverIsBlack && piece != Piece.RedPawn && piece != Piece.RedKing)    continue;

            bool isPawn = piece is Piece.BlackPawn or Piece.RedPawn;

            if (isPawn)
            {
                // Pawns advance one diagonal step forward.
                // Backward = one step in the OPPOSITE direction (toward the pawn's starting side).
                // BlackPawn advances toward y=7 (dy=+1 forward) → backward dy = −1
                // RedPawn   advances toward y=0 (dy=−1 forward) → backward dy = +1
                int dy = (piece == Piece.BlackPawn) ? -1 : +1;

                foreach (int dx in new[] { -1, 1 })
                {
                    int fx = tx + dx, fy = ty + dy;
                    if ((uint)fx > 7 || (uint)fy > 7) continue;
                    if (board.Get(fx, fy) != Piece.Empty) continue; // origin must be free

                    // If the pawn would have been at its promotion row before the move,
                    // it would have been promoted → cross-config, skip.
                    // (BlackPawn can't exist at y=7; RedPawn can't exist at y=0 in any valid position.)
                    // This check is implicitly satisfied since the tablebase only stores valid positions.

                    var pred = board.Clone();
                    pred.Set(tx, ty, Piece.Empty);
                    pred.Set(fx, fy, piece);
                    yield return pred;
                }
            }
            else
            {
                // Kings slide any number of squares diagonally.
                // Backward = came from any empty square along a diagonal, with an unobstructed path.
                foreach (var (ddx, ddy) in new[] { (1,1),(1,-1),(-1,1),(-1,-1) })
                {
                    for (int s = 1; s <= 7; s++)
                    {
                        int fx = tx + ddx * s, fy = ty + ddy * s;
                        if ((uint)fx > 7 || (uint)fy > 7) break;

                        var p = board.Get(fx, fy);
                        if (p != Piece.Empty) break; // path blocked — no further valid origins

                        // (fx,fy) is empty → king could have moved from here to (tx,ty)
                        var pred = board.Clone();
                        pred.Set(tx, ty, Piece.Empty);
                        pred.Set(fx, fy, piece);
                        yield return pred;
                    }
                }
            }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static (int bp, int bk, int rp, int rk) CountPieces(Board board)
    {
        int bp = 0, bk = 0, rp = 0, rk = 0;
        foreach (var p in board.Cells)
            switch (p)
            {
                case Piece.BlackPawn: bp++; break;
                case Piece.BlackKing: bk++; break;
                case Piece.RedPawn:   rp++; break;
                case Piece.RedKing:   rk++; break;
            }
        return (bp, bk, rp, rk);
    }
}
