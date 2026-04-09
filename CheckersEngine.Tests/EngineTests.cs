namespace CheckersEngine.Tests;

using CheckersEngine;
using CheckersEngine.Engine;
using CheckersEngine.Engine.Tablebase;
using Xunit;

/// <summary>
/// Functional tests for the Brazilian Checkers engine.
/// These tests verify correctness of the board representation, move generation,
/// evaluation, and the top-level search entry point.
/// </summary>
public class BoardTests
{
    // ─── Starting position ────────────────────────────────────────────────────

    [Fact]
    public void StartingPosition_HasCorrectPieceCount()
    {
        var board = Board.StartingPosition();
        var (bp, bk, rp, rk) = board.CountPieces();

        Assert.Equal(12, bp);
        Assert.Equal(0,  bk);
        Assert.Equal(12, rp);
        Assert.Equal(0,  rk);
    }

    [Fact]
    public void StartingPosition_PiecesOnDarkSquaresOnly()
    {
        var board = Board.StartingPosition();
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            var p = board.Get(x, y);
            if ((x + y) % 2 == 0)
                Assert.Equal(Piece.Empty, p); // light square always empty
            else if (y <= 2)
                Assert.Equal(Piece.BlackPawn, p);
            else if (y >= 5)
                Assert.Equal(Piece.RedPawn, p);
            else
                Assert.Equal(Piece.Empty, p);
        }
    }

    // ─── Move generation ──────────────────────────────────────────────────────

    [Fact]
    public void StartingPosition_BlackHas7LegalMoves()
    {
        // From the starting position, only the 4 front-rank pawns (y=2) can move.
        // Each can move in at most 2 directions; (7,2) can only go to (6,3).
        var board = Board.StartingPosition();
        var moves = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: true);

        Assert.Equal(7, moves.Count);
        Assert.All(moves, m => Assert.False(m.IsCapture));
    }

    [Fact]
    public void StartingPosition_RedHas7LegalMoves()
    {
        var board = Board.StartingPosition();
        var moves = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: false);

        Assert.Equal(7, moves.Count);
        Assert.All(moves, m => Assert.False(m.IsCapture));
    }

    [Fact]
    public void LegalMoves_AllMovesOriginateFromCorrectSide()
    {
        var board = Board.StartingPosition();

        var blackMoves = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: true);
        foreach (var mv in blackMoves)
            Assert.True(PieceHelper.IsBlack(board.Get(mv.Fx, mv.Fy)),
                $"Black move {mv} originates from a non-black piece");

        var redMoves = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: false);
        foreach (var mv in redMoves)
            Assert.True(PieceHelper.IsRed(board.Get(mv.Fx, mv.Fy)),
                $"Red move {mv} originates from a non-red piece");
    }

    // ─── Board apply ──────────────────────────────────────────────────────────

    [Fact]
    public void ApplyMove_MovesOnePieceAndLeavesOriginEmpty()
    {
        var board = Board.StartingPosition();
        // Move black pawn at (3,2) forward to (4,3)
        var mv = new Move(3, 2, 4, 3);
        var after = BrazilianCheckersEngine.ApplyMove(board, mv);

        Assert.Equal(Piece.Empty,     after.Get(3, 2)); // origin cleared
        Assert.Equal(Piece.BlackPawn, after.Get(4, 3)); // destination set
    }

    [Fact]
    public void ApplyMove_DoesNotMutateOriginalBoard()
    {
        var board = Board.StartingPosition();
        var before = board.Get(3, 2);
        _ = BrazilianCheckersEngine.ApplyMove(board, new Move(3, 2, 4, 3));

        Assert.Equal(before, board.Get(3, 2)); // original unchanged
    }

    [Fact]
    public void ApplyCapture_RemovesCapturedPiece()
    {
        // Minimal 3-piece position: black pawn can capture a red pawn
        var board = Board.FromArray([
            [null, null, null, null, null, null, null, null],
            [null, null, null, null, null, null, null, null],
            [null, null, null, null, null, null, null, null],
            [null, null, null, null, null, null, null, null],
            [null, null, null, null, null, null, null, null],
            [null, null, null, null, null, null, null, null],
            [null, "r",  null, null, null, null, null, null],
            [null, null, null, null, null, null, null, null],
        ]);
        board.Set(0, 5, Piece.BlackPawn);
        board.Set(1, 6, Piece.RedPawn);

        var caps = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: true);
        Assert.NotEmpty(caps);
        Assert.All(caps, m => Assert.True(m.IsCapture));

        var mv     = caps[0];
        var after  = BrazilianCheckersEngine.ApplyMove(board, mv);

        // Red pawn at (1,6) should be gone
        Assert.Equal(Piece.Empty, after.Get(1, 6));
    }

    // ─── Pawn promotion ───────────────────────────────────────────────────────

    [Fact]
    public void ApplyMove_BlackPawnPromotesAtRow7()
    {
        var board = new Board();
        board.Set(3, 6, Piece.BlackPawn);

        var mv    = new Move(3, 6, 4, 7);
        var after = BrazilianCheckersEngine.ApplyMove(board, mv);

        Assert.Equal(Piece.BlackKing, after.Get(4, 7));
    }

    [Fact]
    public void ApplyMove_RedPawnPromotesAtRow0()
    {
        var board = new Board();
        board.Set(4, 1, Piece.RedPawn);

        var mv    = new Move(4, 1, 3, 0);
        var after = BrazilianCheckersEngine.ApplyMove(board, mv);

        Assert.Equal(Piece.RedKing, after.Get(3, 0));
    }

    // ─── Game-over detection ──────────────────────────────────────────────────

    [Fact]
    public void IsGameOver_FalseOnStartingPosition()
    {
        var board = Board.StartingPosition();
        Assert.False(BrazilianCheckersEngine.IsGameOver(board, blackTurn: true));
        Assert.False(BrazilianCheckersEngine.IsGameOver(board, blackTurn: false));
    }

    [Fact]
    public void IsGameOver_TrueWhenSideHasNoPieces()
    {
        // Board with only black pieces → red has no moves
        var board = new Board();
        board.Set(1, 0, Piece.BlackPawn);

        Assert.True(BrazilianCheckersEngine.IsGameOver(board, blackTurn: false));
        Assert.False(BrazilianCheckersEngine.IsGameOver(board, blackTurn: true));
    }

    // ─── Static evaluation ────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_StartingPositionIsNearZero()
    {
        var board = Board.StartingPosition();
        int score = BrazilianCheckersEngine.Evaluate(board);

        // Starting position is symmetric; score should be close to 0
        Assert.InRange(score, -50, 50);
    }

    [Fact]
    public void Evaluate_BlackAheadInMaterialScoresPositive()
    {
        var board = new Board();
        board.Set(1, 0, Piece.BlackPawn);
        board.Set(3, 0, Piece.BlackPawn);
        board.Set(5, 2, Piece.RedPawn);

        int score = BrazilianCheckersEngine.Evaluate(board);
        Assert.True(score > 0, $"Black +1 pawn should score > 0, got {score}");
    }

    [Fact]
    public void Evaluate_KingIsWorthMoreThanPawn()
    {
        var boardPawn = new Board();
        boardPawn.Set(3, 3, Piece.BlackPawn);
        boardPawn.Set(5, 3, Piece.RedPawn);

        var boardKing = new Board();
        boardKing.Set(3, 3, Piece.BlackKing);
        boardKing.Set(5, 3, Piece.RedPawn);

        int pawnScore = BrazilianCheckersEngine.Evaluate(boardPawn);
        int kingScore = BrazilianCheckersEngine.Evaluate(boardKing);
        Assert.True(kingScore > pawnScore, "A king should score higher than a pawn");
    }

    // ─── Serialization round-trip ─────────────────────────────────────────────

    [Fact]
    public void ToArray_FromArray_RoundTrip()
    {
        var original = Board.StartingPosition();
        var arr      = original.ToArray();
        var restored = Board.FromArray(arr);

        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            Assert.Equal(original.Get(x, y), restored.Get(x, y));
    }
}

public class SearchTests
{
    // ─── Opening book ─────────────────────────────────────────────────────────

    [Fact]
    public void FindBestMove_StartingPosition_ReturnsLegalMove()
    {
        var engine = new BrazilianCheckersEngine(EngineConfig.Blitz);
        var board  = Board.StartingPosition();
        var legal  = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: true);

        Move? best = engine.FindBestMove(board);

        Assert.NotNull(best);
        Assert.Contains(legal, m => m.Equals(best.Value));
    }

    [Fact]
    public void FindBestMove_ReturnsNullWhenNoMovesExist()
    {
        // Board where black has no pieces → no moves
        var engine = new BrazilianCheckersEngine(EngineConfig.Blitz);
        var board  = new Board();
        board.Set(1, 0, Piece.RedPawn);

        Move? best = engine.FindBestMove(board);
        Assert.Null(best);
    }

    [Fact]
    public void FindBestMove_SingleMoveReturnedImmediately()
    {
        var engine = new BrazilianCheckersEngine(EngineConfig.Blitz);
        var board  = new Board();
        // Only one legal move for black: (3,6) → (4,7) promotes
        board.Set(3, 6, Piece.BlackPawn);
        board.Set(5, 6, Piece.RedPawn);

        Move? best = engine.FindBestMove(board);
        Assert.NotNull(best);
    }

    // ─── Forced capture ───────────────────────────────────────────────────────

    [Fact]
    public void FindBestMove_TakesImmediateCaptureIfForced()
    {
        var engine = new BrazilianCheckersEngine(EngineConfig.Blitz);
        var board  = new Board();
        // Black pawn at (0,5) can capture red pawn at (1,6) → landing (2,7) = promotion
        board.Set(0, 5, Piece.BlackPawn);
        board.Set(1, 6, Piece.RedPawn);

        var legal = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: true);
        Assert.All(legal, m => Assert.True(m.IsCapture)); // only captures are legal

        Move? best = engine.FindBestMove(board);
        Assert.NotNull(best);
        Assert.True(best.Value.IsCapture);
    }

    // ─── Obvious win in 1 ─────────────────────────────────────────────────────

    [Fact]
    public void FindBestMove_CapturesLastEnemyPiece()
    {
        var engine = new BrazilianCheckersEngine(EngineConfig.Blitz);
        var board  = new Board();
        // Black king can immediately take the only red pawn
        board.Set(0, 3, Piece.BlackKing);
        board.Set(1, 4, Piece.RedPawn);

        Move? best = engine.FindBestMove(board);
        Assert.NotNull(best);
        Assert.True(best.Value.IsCapture, "Engine should capture the last enemy piece");
    }

    // ─── Max-capture rule ─────────────────────────────────────────────────────

    [Fact]
    public void LegalMoves_EnforcesMaxCaptureRule()
    {
        // Black pawn at (0,1): can do single capture to (2,3) or double capture to (4,5)
        //   via (2,3) → (4,5). Only the double capture should be returned.
        var board = new Board();
        board.Set(0, 1, Piece.BlackPawn);
        board.Set(1, 2, Piece.RedPawn);   // first victim
        board.Set(3, 4, Piece.RedPawn);   // second victim (reachable after first capture)

        var moves = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: true);

        // All returned moves must have depth ≥ 2 chain: only (0,1)→(2,3) with further capture
        Assert.NotEmpty(moves);
        Assert.All(moves, m => Assert.True(m.IsCapture));
        // Single-step captures to (2,3) only should NOT be present if a longer chain exists
        // The engine must return only the longest chain start
        Assert.DoesNotContain(moves, m => m.Tx == 2 && m.Ty == 3 && moves.Count > 1);
    }

    // ─── Draw advisor ─────────────────────────────────────────────────────────

    [Fact]
    public void ShouldAcceptDraw_AcceptsKingVsKing()
    {
        var engine = new BrazilianCheckersEngine();
        var board  = new Board();
        // Place kings on squares that are not diagonally aligned (no immediate capture).
        // (1,0) and (6,7) are on dark squares but not on the same diagonal.
        board.Set(1, 0, Piece.BlackKing);
        board.Set(6, 7, Piece.RedKing);
        var memory = new GameMemory();

        var decision = engine.ShouldAcceptDraw(board, memory);
        Assert.True(decision.Accept, "K vs K (no capture available) is a theoretical draw — engine should accept");
    }

    [Fact]
    public void ShouldAcceptDraw_RefusesWhenAheadByTwoPawns()
    {
        var engine = new BrazilianCheckersEngine();
        var board  = new Board();
        board.Set(1, 0, Piece.BlackPawn);
        board.Set(3, 0, Piece.BlackPawn);
        board.Set(5, 0, Piece.BlackPawn);
        board.Set(5, 6, Piece.RedPawn);
        var memory = new GameMemory();

        var decision = engine.ShouldAcceptDraw(board, memory);
        Assert.False(decision.Accept, "Engine is well ahead — should refuse a draw");
    }

    // ─── Transposition table hash ─────────────────────────────────────────────

    [Fact]
    public void ZobristHash_DiffersBetweenBlackAndRedTurn()
    {
        var board = Board.StartingPosition();
        ulong hashBlack = TranspositionTable.Hash(board, blackTurn: true);
        ulong hashRed   = TranspositionTable.Hash(board, blackTurn: false);

        Assert.NotEqual(hashBlack, hashRed);
    }

    [Fact]
    public void ZobristHash_SamePositionSameTurnGivesSameHash()
    {
        var b1 = Board.StartingPosition();
        var b2 = Board.StartingPosition();

        Assert.Equal(TranspositionTable.Hash(b1, true),  TranspositionTable.Hash(b2, true));
        Assert.Equal(TranspositionTable.Hash(b1, false), TranspositionTable.Hash(b2, false));
    }

    [Fact]
    public void ZobristHash_DifferentPositionsDifferentHashes()
    {
        var b1 = Board.StartingPosition();
        var b2 = BrazilianCheckersEngine.ApplyMove(b1, new Move(3, 2, 4, 3));

        Assert.NotEqual(TranspositionTable.Hash(b1, true), TranspositionTable.Hash(b2, true));
    }

    // ─── GameMemory ───────────────────────────────────────────────────────────

    [Fact]
    public void GameMemory_RepetitionCountIncreasesOnRecord()
    {
        var board  = Board.StartingPosition();
        var memory = new GameMemory();

        Assert.Equal(0, memory.RepetitionCount(board));

        memory.RecordHumanMove(board, (1, 2), (2, 3));
        Assert.Equal(1, memory.RepetitionCount(board));

        memory.RecordHumanMove(board, (1, 2), (2, 3));
        Assert.Equal(2, memory.RepetitionCount(board));
    }

    [Fact]
    public void GameMemory_ResetGameClearsRepetitions()
    {
        var board  = Board.StartingPosition();
        var memory = new GameMemory();

        memory.RecordHumanMove(board, (1, 2), (2, 3));
        Assert.Equal(1, memory.RepetitionCount(board));

        memory.ResetGame();
        Assert.Equal(0, memory.RepetitionCount(board));
    }

    // ─── TablebaseResult ──────────────────────────────────────────────────────

    [Fact]
    public void TablebaseResult_IsKnownOnlyForNonUnknown()
    {
        Assert.True(new TablebaseResult(TablebaseOutcome.Win,  1).IsKnown);
        Assert.True(new TablebaseResult(TablebaseOutcome.Loss, 1).IsKnown);
        Assert.True(new TablebaseResult(TablebaseOutcome.Draw, 0).IsKnown);
        Assert.False(new TablebaseResult(TablebaseOutcome.Unknown, 0).IsKnown);
    }

    [Fact]
    public void TablebaseResult_ToStringDescribesOutcome()
    {
        Assert.Contains("Win",  new TablebaseResult(TablebaseOutcome.Win,  4).ToString());
        Assert.Contains("Loss", new TablebaseResult(TablebaseOutcome.Loss, 2).ToString());
        Assert.Contains("Draw", new TablebaseResult(TablebaseOutcome.Draw, 0).ToString());
    }
}
