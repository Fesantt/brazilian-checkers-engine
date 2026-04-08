namespace CheckersEngine.Engine;

/// <summary>
/// Represents a single cell on the board.
/// Byte-sized enum so the flat <c>Piece[64]</c> board array stays cache-friendly.
/// </summary>
public enum Piece : byte
{
    /// <summary>Empty square.</summary>
    Empty     = 0,

    /// <summary>Red pawn ('r') — human player, advances from y=7 toward y=0.</summary>
    RedPawn   = 1,

    /// <summary>Red king ('R') — human player's promoted piece.</summary>
    RedKing   = 2,

    /// <summary>Black pawn ('b') — bot/engine player, advances from y=0 toward y=7.</summary>
    BlackPawn = 3,

    /// <summary>Black king ('B') — engine's promoted piece.</summary>
    BlackKing = 4,

    /// <summary>
    /// Internal sentinel used during chain-capture depth calculation so that
    /// kings cannot re-cross an already-captured diagonal square.
    /// Never written to the persistent board state.
    /// </summary>
    Sentinel  = 5,
}

/// <summary>Utility methods for <see cref="Piece"/> classification.</summary>
public static class PieceHelper
{
    /// <summary>Returns <c>true</c> if the piece belongs to the red (human) side.</summary>
    public static bool IsRed(Piece p)   => p == Piece.RedPawn   || p == Piece.RedKing;

    /// <summary>Returns <c>true</c> if the piece belongs to the black (engine) side.</summary>
    public static bool IsBlack(Piece p) => p == Piece.BlackPawn || p == Piece.BlackKing;

    /// <summary>Returns <c>true</c> if the piece is a king (either color).</summary>
    public static bool IsKing(Piece p)  => p == Piece.RedKing   || p == Piece.BlackKing;

    /// <summary>
    /// Returns <c>true</c> if the piece belongs to the side currently moving.
    /// </summary>
    /// <param name="p">The piece to test.</param>
    /// <param name="blackTurn"><c>true</c> → test for black; <c>false</c> → test for red.</param>
    public static bool IsMine(Piece p, bool blackTurn)  => blackTurn ? IsBlack(p) : IsRed(p);

    /// <summary>
    /// Returns <c>true</c> if the piece belongs to the opponent of the side currently moving.
    /// </summary>
    public static bool IsEnemy(Piece p, bool blackTurn) => blackTurn ? IsRed(p) : IsBlack(p);

    /// <summary>
    /// Parses a single-character string representation into a <see cref="Piece"/>.
    /// </summary>
    /// <param name="s">One of: <c>"r"</c>, <c>"R"</c>, <c>"b"</c>, <c>"B"</c>, or <c>null</c>/empty.</param>
    public static Piece FromString(string? s) => s switch {
        "r" => Piece.RedPawn,
        "R" => Piece.RedKing,
        "b" => Piece.BlackPawn,
        "B" => Piece.BlackKing,
        _   => Piece.Empty,
    };

    /// <summary>Converts a piece back to its single-character string representation.</summary>
    public static string? ToString(Piece p) => p switch {
        Piece.RedPawn   => "r",
        Piece.RedKing   => "R",
        Piece.BlackPawn => "b",
        Piece.BlackKing => "B",
        _               => null,
    };
}
