namespace CheckersEngine.Engine;

using CheckersEngine.Engine.Tablebase;

/// <summary>
/// Brazilian checkers search engine.
/// </summary>
/// <remarks>
/// <b>Algorithms:</b>
/// <list type="bullet">
///   <item>Iterative deepening with time-based cutoff.</item>
///   <item>Principal Variation Search (PVS / Negascout).</item>
///   <item>Aspiration windows — narrow the α-β window around the previous score.</item>
///   <item>Transposition table (Zobrist, configurable size, always-replace).</item>
///   <item>Quiescence search — resolves captures at leaf nodes (MVV ordering).</item>
///   <item>Killer moves — 2 quiet moves per ply remembered across iterations.</item>
///   <item>History heuristic — tracks quiet moves that caused cut-offs.</item>
///   <item>Late Move Reductions (LMR) — reduces depth of late-ordered quiet moves.</item>
///   <item>Opening book — hardcoded Brazilian checkers standard openings.</item>
/// </list>
/// Score convention: positive = black (engine) winning; negative = red (human) winning.
/// </remarks>
public sealed class Search
{
    private readonly EngineConfig        _cfg;
    private readonly TranspositionTable  _tt;
    private readonly EndgameTablebase?   _tablebase;
    private readonly Move[,]             _killers = new Move[64, 2];
    private readonly int[,,,]            _history = new int[8, 8, 8, 8];

    // Simple in-search repetition tracker: hashes along the current search path.
    // Prevents the engine from playing into known repeated positions during search.
    private readonly ulong[] _repPath = new ulong[128];
    private int _repLen;

    private static readonly Exception Timeout = new TimeoutException("search timeout");

    // ─── Opening book ─────────────────────────────────────────────────────────
    // (minTotal, maxTotal, move) — total piece count range gates applicability;
    // the move is only played if it is actually legal in the current position.
    // All moves use dark squares (x+y is odd), matching the Brazilian checkers layout.

    private static readonly (int minTotal, int maxTotal, Move mv)[] Book =
    [
        // 24 pieces — first move; only front-rank pawns (y=2) can move: x ∈ {1,3,5,7}
        (24, 24, new Move(3, 2, 4, 3)),   // center right — most popular
        (24, 24, new Move(5, 2, 4, 3)),   // center left  — equally strong
        (24, 24, new Move(3, 2, 2, 3)),   // semi-center left
        (24, 24, new Move(5, 2, 6, 3)),   // semi-center right

        // 22–23 pieces — early follow-ups
        (22, 23, new Move(1, 2, 2, 3)),   // left flank pawn forward
        (22, 23, new Move(7, 2, 6, 3)),   // right flank pawn forward
        (22, 23, new Move(3, 0, 4, 1)),   // back-rank pawn, supports center
        (22, 23, new Move(5, 0, 4, 1)),   // back-rank pawn, supports center

        // 20–21 pieces — mid-opening development
        (20, 21, new Move(1, 0, 2, 1)),   // back-rank left development
        (20, 21, new Move(7, 0, 6, 1)),   // back-rank right development
        (20, 21, new Move(5, 2, 6, 3)),   // right pawn if not yet advanced

        // 18–19 pieces — later opening
        (18, 19, new Move(0, 1, 1, 2)),   // left-side development
        (18, 19, new Move(4, 1, 5, 2)),   // center development
    ];

    /// <summary>Creates a search engine using the supplied configuration.</summary>
    /// <param name="cfg">Engine configuration.</param>
    /// <param name="tablebase">
    /// Optional endgame tablebase. When provided, it is probed inside the search tree
    /// for any position with few enough pieces, returning exact results instantly.
    /// </param>
    public Search(EngineConfig cfg, EndgameTablebase? tablebase = null)
    {
        _cfg       = cfg;
        _tablebase = tablebase;
        _tt        = new TranspositionTable(cfg.TranspositionTableSizePow2);
    }

    // ─── Entry point ─────────────────────────────────────────────────────────

    /// <summary>
    /// Chooses the best move for black (engine side).
    /// </summary>
    /// <param name="board">Current position.</param>
    /// <param name="activePieceX">
    /// If a chain capture is in progress, restricts moves to those originating
    /// from this column. Pass <c>null</c> for a normal (non-chain) turn.
    /// </param>
    /// <param name="activePieceY">Row of the active piece (see <paramref name="activePieceX"/>).</param>
    /// <param name="thinkingMs">
    /// Time budget in milliseconds. Overrides <see cref="EngineConfig.ThinkingMs"/> when > 0.
    /// Pass 0 to use the value from config.
    /// </param>
    /// <returns>The best <see cref="Move"/>, or <c>null</c> if no legal moves exist.</returns>
    public Move? FindBestMove(Board board, int? activePieceX, int? activePieceY, int thinkingMs = 0)
    {
        int budget = thinkingMs > 0 ? thinkingMs : _cfg.ThinkingMs;

        var moves = GetRootMoves(board, activePieceX, activePieceY);
        if (moves.Count == 0) return null;
        if (moves.Count == 1) return moves[0];

        // Opening book — instant response on known openings
        if (_cfg.UseOpeningBook && activePieceX == null)
        {
            var (bp, bk, rp, rk) = board.CountPieces();
            int total = bp + bk + rp + rk;
            foreach (var (minT, maxT, bmv) in Book)
            {
                if (total < minT || total > maxT) continue;
                if (moves.Any(m => m.Fx == bmv.Fx && m.Fy == bmv.Fy && m.Tx == bmv.Tx && m.Ty == bmv.Ty))
                    return bmv;
            }
        }

        long deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + budget;

        _tt.Clear();
        Array.Clear(_killers);
        Array.Clear(_history);
        _repLen = 0;

        Move bestMove  = moves[0];
        int  bestScore = Evaluator.LoseScore(0);
        int  prevScore = 0;
        int  window    = _cfg.AspirationWindowInitial;

        for (int depth = 1; depth <= _cfg.MaxDepth; depth++)
        {
            int alpha, beta;
            if (_cfg.UseAspirationWindows && depth > 3)
            {
                alpha = prevScore - window;
                beta  = prevScore + window;
            }
            else
            {
                alpha = int.MinValue / 2;
                beta  = int.MaxValue / 2;
            }

            retry:
            Move iterBest = moves[0];
            int  iterScore = int.MinValue / 2;
            bool timedOut  = false;

            try
            {
                bool pvNode = true;
                for (int i = 0; i < moves.Count; i++)
                {
                    var mv = moves[i];
                    var (nb, hasFurther) = ApplyRoot(board, mv);

                    int score;
                    if (pvNode)
                    {
                        score    = hasFurther
                            ? PVS(nb, depth,     alpha, beta, true,  deadline, 1)
                            : PVS(nb, depth - 1, alpha, beta, false, deadline, 1);
                        pvNode = false;
                    }
                    else
                    {
                        int nwAlpha = alpha;
                        score = hasFurther
                            ? PVS(nb, depth,     nwAlpha, nwAlpha + 1, true,  deadline, 1)
                            : PVS(nb, depth - 1, nwAlpha, nwAlpha + 1, false, deadline, 1);

                        if (score > nwAlpha && score < beta)
                            score = hasFurther
                                ? PVS(nb, depth,     alpha, beta, true,  deadline, 1)
                                : PVS(nb, depth - 1, alpha, beta, false, deadline, 1);
                    }

                    if (score > iterScore) { iterScore = score; iterBest = mv; }
                    if (score > alpha)       alpha = score;
                }

                // Re-sort root moves using TT score for next iteration
                ulong rootHash = TranspositionTable.Hash(board, true);
                _tt.TryGetMove(rootHash, out var ttRootMove);
                moves.Sort((a, b) =>
                    MoveScore(board, b, 0, ttRootMove).CompareTo(MoveScore(board, a, 0, ttRootMove)));
            }
            catch (Exception e) when (e == Timeout)
            {
                timedOut = true;
            }

            if (!timedOut)
            {
                // Aspiration window failure handling
                if (_cfg.UseAspirationWindows && depth > 3)
                {
                    if (iterScore <= prevScore - window)
                    {
                        window = Math.Min(window * 4, _cfg.AspirationWindowMax);
                        alpha  = iterScore - window;
                        beta   = prevScore + window;
                        goto retry;
                    }
                    if (iterScore >= prevScore + window)
                    {
                        window = Math.Min(window * 4, _cfg.AspirationWindowMax);
                        beta   = iterScore + window;
                        alpha  = prevScore - window;
                        goto retry;
                    }
                }

                prevScore = iterScore;
                window    = _cfg.AspirationWindowInitial;

                if (depth >= _cfg.MinDepth || iterScore >= Evaluator.WinBase)
                {
                    bestMove  = iterBest;
                    bestScore = iterScore;
                }

                if (bestScore >= Evaluator.WinBase) break; // forced mate found
            }
            else
            {
                break;
            }

            if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline) break;
        }

        return bestMove;
    }

    // ─── PVS ─────────────────────────────────────────────────────────────────

    private int PVS(Board board, int depth, int alpha, int beta,
                    bool blackTurn, long deadline, int ply)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
            throw Timeout;

        ulong hash = TranspositionTable.Hash(board, blackTurn);

        // In-search repetition detection: returning 0 (draw) prevents the engine
        // from cycling into positions it has already visited on this path.
        if (ply > 0)
        {
            for (int r = _repLen - 1; r >= 0; r--)
                if (_repPath[r] == hash) return 0;
        }

        if (_tt.TryGet(hash, depth, alpha, beta, out int cached, out _))
            return cached;

        // Probe endgame tablebase for an instant exact result on covered positions.
        if (_tablebase != null && depth > 0)
        {
            var tbr = _tablebase.Probe(board, blackTurn);
            if (tbr != null)
            {
                int magnitude = Evaluator.WinBase + 50 - tbr.Value.Dtm;
                int tbScore;
                if (tbr.Value.Outcome == TablebaseOutcome.Draw)
                    tbScore = 0;
                else
                {
                    bool blackWins = (blackTurn  && tbr.Value.Outcome == TablebaseOutcome.Win)
                                  || (!blackTurn && tbr.Value.Outcome == TablebaseOutcome.Loss);
                    tbScore = blackWins ? magnitude : -magnitude;
                }
                _tt.Store(hash, depth, tbScore, TtFlag.Exact, default);
                return tbScore;
            }
        }

        var moves = MoveGenerator.GetLegalMoves(board, blackTurn);

        if (moves.Count == 0)
        {
            int val = blackTurn ? Evaluator.LoseScore(depth) : Evaluator.WinScore(depth);
            _tt.Store(hash, depth, val, TtFlag.Exact, default);
            return val;
        }

        if (depth <= 0)
        {
            return _cfg.UseQuiescence
                ? Quiesce(board, alpha, beta, blackTurn, deadline, ply)
                : Evaluator.Evaluate(board);
        }

        OrderMoves(board, moves, ply, hash);

        // Push this position onto the repetition path before exploring children
        if (_repLen < _repPath.Length) _repPath[_repLen++] = hash;

        TtFlag flag    = blackTurn ? TtFlag.Upper : TtFlag.Lower;
        Move bestMove  = moves[0];
        int  best      = blackTurn ? int.MinValue / 2 : int.MaxValue / 2;

        for (int i = 0; i < moves.Count; i++)
        {
            var mv = moves[i];
            var piece = board.Get(mv.Fx, mv.Fy);
            bool wouldPromote = (piece == Piece.BlackPawn && mv.Ty == 7) ||
                                (piece == Piece.RedPawn   && mv.Ty == 0);
            bool skipPromo = wouldPromote && mv.IsCapture;

            var nb = board.Apply(mv, skipPromo);
            bool hasFurther = mv.IsCapture && MoveGenerator.GetAllCaptures(nb, blackTurn)
                                                           .Any(c => c.Fx == mv.Tx && c.Fy == mv.Ty);
            if (skipPromo && !hasFurther)
                nb.Set(mv.Tx, mv.Ty, blackTurn ? Piece.BlackKing : Piece.RedKing);

            int  nextDepth = hasFurther ? depth     : depth - 1;
            bool nextBlack = hasFurther ? blackTurn : !blackTurn;
            int  nextPly   = hasFurther ? ply       : ply + 1;

            // LMR — reduce late quiet moves
            bool lmr = _cfg.UseLMR &&
                       i >= _cfg.LmrMinMoveIndex &&
                       !mv.IsCapture &&
                       depth >= _cfg.LmrMinDepth &&
                       nextDepth > 0;
            int reduction = lmr ? (i >= _cfg.LmrAggressiveIndex ? 2 : 1) : 0;

            int score;

            if (blackTurn) // maximizing
            {
                if (i == 0)
                    score = PVS(nb, nextDepth, alpha, beta, nextBlack, deadline, nextPly);
                else
                {
                    score = PVS(nb, nextDepth - reduction, alpha, alpha + 1, nextBlack, deadline, nextPly);
                    // Re-search at full window if the null-window search improved alpha,
                    // or if LMR reduced depth may have produced an imprecise fail-high.
                    if (score > alpha && (score < beta || lmr))
                        score = PVS(nb, nextDepth, alpha, beta, nextBlack, deadline, nextPly);
                }

                if (score > best)  { best = score; bestMove = mv; }
                if (score > alpha) { alpha = score; flag = TtFlag.Exact; }
                if (alpha >= beta)
                {
                    if (!mv.IsCapture) UpdateQuietHeuristics(mv, ply, depth);
                    _tt.Store(hash, depth, best, TtFlag.Lower, bestMove);
                    _repLen--;
                    return best;
                }
            }
            else // minimizing
            {
                if (i == 0)
                    score = PVS(nb, nextDepth, alpha, beta, nextBlack, deadline, nextPly);
                else
                {
                    score = PVS(nb, nextDepth - reduction, beta - 1, beta, nextBlack, deadline, nextPly);
                    // Re-search at full window whenever the null-window result is within
                    // the interesting range (score < beta). Fix: the previous condition
                    // incorrectly skipped re-searches when score ≤ alpha, missing moves
                    // that are very good for the minimizer.
                    if (score < beta)
                        score = PVS(nb, nextDepth, alpha, beta, nextBlack, deadline, nextPly);
                }

                if (score < best)  { best = score; bestMove = mv; }
                if (score < beta)  { beta  = score; flag = TtFlag.Exact; }
                if (alpha >= beta)
                {
                    if (!mv.IsCapture) UpdateQuietHeuristics(mv, ply, depth);
                    _tt.Store(hash, depth, best, TtFlag.Upper, bestMove);
                    _repLen--;
                    return best;
                }
            }
        }

        _tt.Store(hash, depth, best, flag, bestMove);
        _repLen--;
        return best;
    }

    // ─── Quiescence search ────────────────────────────────────────────────────

    private int Quiesce(Board board, int alpha, int beta, bool blackTurn, long deadline, int ply)
    {
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline) throw Timeout;

        int stand = Evaluator.Evaluate(board);

        if (blackTurn)
        {
            if (stand >= beta)  return beta;
            if (stand > alpha)  alpha = stand;
        }
        else
        {
            if (stand <= alpha) return alpha;
            if (stand < beta)   beta = stand;
        }

        var allMoves = MoveGenerator.GetLegalMoves(board, blackTurn);
        var caps = allMoves.Where(m => m.IsCapture).ToList();
        if (caps.Count == 0) return stand;

        caps.Sort((a, b) => CaptureScore(board, b).CompareTo(CaptureScore(board, a)));

        foreach (var mv in caps)
        {
            var piece = board.Get(mv.Fx, mv.Fy);
            bool wouldPromote = (piece == Piece.BlackPawn && mv.Ty == 7) ||
                                (piece == Piece.RedPawn   && mv.Ty == 0);

            var  nb         = board.Apply(mv, wouldPromote);
            bool hasFurther = MoveGenerator.GetAllCaptures(nb, blackTurn)
                                           .Any(c => c.Fx == mv.Tx && c.Fy == mv.Ty);
            if (wouldPromote && !hasFurther)
                nb.Set(mv.Tx, mv.Ty, blackTurn ? Piece.BlackKing : Piece.RedKing);

            bool nextBlack = hasFurther ? blackTurn : !blackTurn;
            int  score     = Quiesce(nb, alpha, beta, nextBlack, deadline, ply + 1);

            if (blackTurn)
            {
                if (score >= beta) return beta;
                if (score > alpha) alpha = score;
            }
            else
            {
                if (score <= alpha) return alpha;
                if (score < beta)   beta = score;
            }
        }

        return blackTurn ? alpha : beta;
    }

    // ─── Move ordering ────────────────────────────────────────────────────────

    private void OrderMoves(Board board, List<Move> moves, int ply, ulong hash)
    {
        _tt.TryGetMove(hash, out var ttMove);
        moves.Sort((a, b) =>
            MoveScore(board, b, ply, ttMove).CompareTo(MoveScore(board, a, ply, ttMove)));
    }

    private int MoveScore(Board board, in Move mv, int ply, in Move ttMove)
    {
        if (mv.Equals(ttMove)) return 1_000_000;
        if (mv.IsCapture)      return 100_000 + CaptureScore(board, mv);

        if (_cfg.UseKillerMoves && ply < 64)
        {
            if (_killers[ply, 0].Equals(mv)) return 90_000;
            if (_killers[ply, 1].Equals(mv)) return 80_000;
        }

        return _cfg.UseHistoryHeuristic ? _history[mv.Fx, mv.Fy, mv.Tx, mv.Ty] : 0;
    }

    private static int CaptureScore(Board board, in Move mv)
    {
        int dx    = Math.Sign(mv.Tx - mv.Fx);
        int dy    = Math.Sign(mv.Ty - mv.Fy);
        int steps = Math.Abs(mv.Tx - mv.Fx);
        for (int i = 1; i < steps; i++)
        {
            int cx = mv.Fx + dx * i, cy = mv.Fy + dy * i;
            var p  = board.Get(cx, cy);
            if (p != Piece.Empty && p != Piece.Sentinel)
                return PieceHelper.IsKing(p) ? Evaluator.KingVal : Evaluator.PawnVal;
        }
        return Evaluator.PawnVal;
    }

    private void UpdateQuietHeuristics(in Move mv, int ply, int depth)
    {
        if (_cfg.UseKillerMoves && ply < 64)
        {
            _killers[ply, 1] = _killers[ply, 0];
            _killers[ply, 0] = mv;
        }
        if (_cfg.UseHistoryHeuristic)
            _history[mv.Fx, mv.Fy, mv.Tx, mv.Ty] += depth * depth;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static List<Move> GetRootMoves(Board board, int? apx, int? apy)
    {
        var all = MoveGenerator.GetLegalMoves(board, true);
        if (apx.HasValue && apy.HasValue)
        {
            var chain = all.Where(m => m.IsCapture && m.Fx == apx.Value && m.Fy == apy.Value).ToList();
            if (chain.Count > 0) return chain;
            var anyCapture = all.Where(m => m.IsCapture).ToList();
            if (anyCapture.Count > 0) return anyCapture;
        }
        return all;
    }

    private static (Board nb, bool hasFurther) ApplyRoot(Board board, Move mv)
    {
        var piece = board.Get(mv.Fx, mv.Fy);
        bool wouldPromote = (piece == Piece.BlackPawn && mv.Ty == 7) ||
                            (piece == Piece.RedPawn   && mv.Ty == 0);
        bool skipPromo = wouldPromote && mv.IsCapture;
        var  nb = board.Apply(mv, skipPromo);
        bool hf = mv.IsCapture && MoveGenerator.GetAllCaptures(nb, true)
                                               .Any(c => c.Fx == mv.Tx && c.Fy == mv.Ty);
        if (skipPromo && !hf) nb.Set(mv.Tx, mv.Ty, Piece.BlackKing);
        return (nb, hf);
    }
}
