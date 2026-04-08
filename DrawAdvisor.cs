namespace CheckersEngine;

using CheckersEngine.Engine;

/// <summary>
/// Heuristic draw decision logic — runs entirely in the calling thread (no minimax).
/// </summary>
/// <remarks>
/// Uses fast positional analysis (material, threats, captures, repetitions) to decide
/// whether the engine should accept or offer a draw without consulting the search engine.
/// Thresholds are controlled by <see cref="EngineConfig"/>.
///
/// <b>Score convention:</b> positive = black (engine) ahead; negative = red (human) ahead.
/// </remarks>
public static class DrawAdvisor
{
    // ─── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluates whether the engine should accept a draw offer proposed by the human.
    /// </summary>
    /// <param name="board">Current board state.</param>
    /// <param name="memory">Game memory for repetition counts.</param>
    /// <param name="cfg">Engine configuration (thresholds).</param>
    /// <returns>A <see cref="DrawDecision"/> with <c>Accept</c> and a human-readable reason.</returns>
    public static DrawDecision ShouldAcceptDraw(Board board, GameMemory memory, EngineConfig cfg)
    {
        var p = Analyze(board, memory);

        // Bot is clearly winning — refuse first, before everything else
        if (p.MatScore >= cfg.DrawRefuseAboveScore)
            return new DrawDecision(false, "A Máquina está vencendo — ela recusa o empate!");

        if (p.BPawnsNearPromo > 0)
            return new DrawDecision(false, "A Máquina está prestes a coroar uma peça — ela recusa o empate!");

        // Has a strong capture with equal or better position
        if (p.BotCaptures && p.MatScore >= 0)
            return new DrawDecision(false, "A Máquina tem um lance forte — ela recusa o empate!");

        // Known theoretical draws (only reached without clear advantage above)
        if (p.IsKvK)
            return new DrawDecision(true, "draw_known");

        if (p.OnlyKings && p.BotKings <= p.HumKings)
            return new DrawDecision(true, "draw_known");

        // Slight advantage but no immediate resource — keep trying
        if (p.MatScore >= 0)
            return new DrawDecision(false, "A Máquina ainda acredita que pode vencer — ela recusa o empate!");

        // Locked/repetitive position with balanced material
        if (p.Reps >= cfg.DrawRepetitionThreshold && Math.Abs(p.MatScore) <= cfg.DrawRefuseAboveScore / 2)
            return new DrawDecision(true, "draw_stuck");

        // Clearly losing without a capture to fight back
        if (p.MatScore <= cfg.DrawAcceptBelowScore && !p.BotCaptures)
            return new DrawDecision(true, "draw_losing");

        // Losing badly even with a capture (capture won't recover the deficit)
        if (p.MatScore <= cfg.DrawAcceptBelowScore - 100)
            return new DrawDecision(true, "draw_losing");

        // Outnumbered 3-to-1
        if (p.BotPieces == 1 && p.HumPieces >= 3)
            return new DrawDecision(true, "draw_losing");

        // All bot pieces threatened and no counter-play
        if (p.BotThreats >= p.BotPieces && !p.BotCaptures)
            return new DrawDecision(true, "draw_losing");

        return new DrawDecision(false, "A Máquina ainda acredita que pode virar o jogo — ela recusa o empate!");
    }

    /// <summary>
    /// Evaluates whether the engine should proactively offer a draw to the human.
    /// Call this after the engine moves, before returning the turn to the human.
    /// </summary>
    /// <param name="board">Board state after the engine's move.</param>
    /// <param name="memory">Game memory for repetition counts.</param>
    /// <param name="cfg">Engine configuration (thresholds).</param>
    /// <returns>A <see cref="DrawOffer"/> with <c>ShouldOffer</c> and an optional message.</returns>
    public static DrawOffer ShouldOfferDraw(Board board, GameMemory memory, EngineConfig cfg)
    {
        var p = Analyze(board, memory);

        // Bot is winning — never offer
        if (p.MatScore >= cfg.DrawRefuseAboveScore / 2)
            return new DrawOffer(false, null);

        if (p.BPawnsNearPromo > 0)
            return new DrawOffer(false, null);

        if (p.BotCaptures && p.MatScore >= cfg.DrawAcceptBelowScore / 2)
            return new DrawOffer(false, null);

        // Known theoretical draws
        if (p.IsKvK)
            return new DrawOffer(true, "A posição é tecnicamente empatada. A Máquina propõe empate.");

        if (p.OnlyKings && p.BotKings == p.HumKings)
            return new DrawOffer(true, "A Máquina propõe empate — posição de damas equilibrada.");

        // Outnumbered with repeated position
        if (p.BotPieces == 1 && p.HumPieces >= 3 && p.Reps >= 1)
            return new DrawOffer(true, "A Máquina está em desvantagem e propõe empate.");

        // Locked + growing disadvantage
        if (p.Reps >= 3 && p.MatScore <= cfg.DrawAcceptBelowScore / 2)
            return new DrawOffer(true, "A posição está travada. A Máquina propõe empate.");

        if (p.Reps >= 2 && p.MatScore <= cfg.DrawAcceptBelowScore)
            return new DrawOffer(true, "A Máquina está em desvantagem e propõe empate.");

        // Severe losing position without any counter
        if (p.MatScore <= cfg.DrawAcceptBelowScore * 2 && !p.BotCaptures)
            return new DrawOffer(true, "A Máquina propõe empate — posição desfavorável.");

        return new DrawOffer(false, null);
    }

    // ─── Internal analysis ────────────────────────────────────────────────────

    private static PositionMetrics Analyze(Board board, GameMemory memory)
    {
        int bp = 0, bk = 0, rp = 0, rk = 0;
        int bPawnsNearPromo = 0;

        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            switch (board.Get(x, y))
            {
                case Piece.BlackPawn: bp++; if (y >= 6) bPawnsNearPromo++; break;
                case Piece.BlackKing: bk++; break;
                case Piece.RedPawn:   rp++; break;
                case Piece.RedKing:   rk++; break;
            }
        }

        int total    = bp + bk + rp + rk;
        int matScore = (bp * Evaluator.PawnVal + bk * Evaluator.KingVal) -
                       (rp * Evaluator.PawnVal + rk * Evaluator.KingVal);

        return new PositionMetrics(
            BotPawns        : bp,
            BotKings        : bk,
            HumPawns        : rp,
            HumKings        : rk,
            Total           : total,
            MatScore        : matScore,
            BPawnsNearPromo : bPawnsNearPromo,
            BotCaptures     : HasCaptures(board, true),
            BotThreats      : CountThreatenedPieces(board, true),
            Reps            : memory.RepetitionCount(board)
        );
    }

    private static bool HasCaptures(Board board, bool botSide)
    {
        for (int fy = 0; fy < 8; fy++)
        for (int fx = 0; fx < 8; fx++)
        {
            var piece = board.Get(fx, fy);
            if (piece == Piece.Empty || !PieceHelper.IsMine(piece, botSide)) continue;

            if (PieceHelper.IsKing(piece))
            {
                foreach (var (dx, dy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                {
                    bool found = false;
                    for (int s = 1; s <= 7; s++)
                    {
                        int nx = fx + dx * s, ny = fy + dy * s;
                        if ((uint)nx > 7 || (uint)ny > 7) break;
                        var p = board.Get(nx, ny);
                        if      (p == Piece.Empty)                             { if (found) return true; }
                        else if (PieceHelper.IsEnemy(p, botSide) && !found)   found = true;
                        else                                                   break;
                    }
                }
            }
            else
            {
                foreach (var (dx, dy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                {
                    int mx = fx + dx, my = fy + dy, tx = fx + dx * 2, ty = fy + dy * 2;
                    if ((uint)tx > 7 || (uint)ty > 7) continue;
                    if (PieceHelper.IsEnemy(board.Get(mx, my), botSide) && board.Get(tx, ty) == Piece.Empty)
                        return true;
                }
            }
        }
        return false;
    }

    private static int CountThreatenedPieces(Board board, bool botSide)
    {
        var threatened = new HashSet<int>();

        for (int fy = 0; fy < 8; fy++)
        for (int fx = 0; fx < 8; fx++)
        {
            var piece = board.Get(fx, fy);
            if (piece == Piece.Empty || !PieceHelper.IsEnemy(piece, botSide)) continue;

            if (PieceHelper.IsKing(piece))
            {
                foreach (var (dx, dy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                {
                    Piece? enemy = null;
                    int ex = 0, ey = 0;
                    for (int s = 1; s <= 7; s++)
                    {
                        int nx = fx + dx * s, ny = fy + dy * s;
                        if ((uint)nx > 7 || (uint)ny > 7) break;
                        var p = board.Get(nx, ny);
                        if (p == Piece.Empty)
                        {
                            if (enemy.HasValue) threatened.Add(ey * 8 + ex);
                        }
                        else if (PieceHelper.IsMine(p, botSide) && !enemy.HasValue)
                        {
                            enemy = p; ex = nx; ey = ny;
                        }
                        else break;
                    }
                }
            }
            else
            {
                foreach (var (dx, dy) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                {
                    int mx = fx + dx, my = fy + dy, tx = fx + dx * 2, ty = fy + dy * 2;
                    if ((uint)tx > 7 || (uint)ty > 7) continue;
                    if (PieceHelper.IsMine(board.Get(mx, my), botSide) && board.Get(tx, ty) == Piece.Empty)
                        threatened.Add(my * 8 + mx);
                }
            }
        }

        return threatened.Count;
    }

    // ─── Metrics ─────────────────────────────────────────────────────────────

    private readonly record struct PositionMetrics(
        int  BotPawns,
        int  BotKings,
        int  HumPawns,
        int  HumKings,
        int  Total,
        int  MatScore,
        int  BPawnsNearPromo,
        bool BotCaptures,
        int  BotThreats,
        int  Reps)
    {
        public int  BotPieces => BotPawns + BotKings;
        public int  HumPieces => HumPawns + HumKings;
        public bool OnlyKings => BotPawns == 0 && HumPawns == 0;
        public bool IsKvK     => Total == 2 && BotKings == 1 && HumKings == 1;
    }
}

/// <summary>Result of a draw-acceptance evaluation.</summary>
/// <param name="Accept">Whether the engine accepts the draw.</param>
/// <param name="Reason">
/// Human-readable reason (Portuguese) — suitable for display to the player.
/// For internal reasons use: <c>"draw_known"</c>, <c>"draw_stuck"</c>, <c>"draw_losing"</c>.
/// </param>
public readonly record struct DrawDecision(bool Accept, string Reason);

/// <summary>Result of a draw-offer evaluation.</summary>
/// <param name="ShouldOffer">Whether the engine should proactively offer a draw.</param>
/// <param name="Message">
/// Human-readable offer message (Portuguese) to display to the player, or <c>null</c> if not offering.
/// </param>
public readonly record struct DrawOffer(bool ShouldOffer, string? Message);
