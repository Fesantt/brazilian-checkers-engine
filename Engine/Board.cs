using System.Text.Json;

namespace CheckersEngine.Engine;

/// <summary>
/// Immutable-by-convention 8×8 board state.
/// </summary>
/// <remarks>
/// Cells are stored in a flat <c>Piece[64]</c> array indexed by <c>y * 8 + x</c>.
/// This layout is cache-friendly and avoids the double-indirection of a jagged array.
/// <para>
/// Mutation is done by <see cref="Apply"/> which returns a new <see cref="Board"/>
/// (copy-on-write), keeping the search tree free of aliasing bugs.
/// </para>
/// </remarks>
public sealed class Board
{
    /// <summary>
    /// Flat cell array. Index = <c>y * 8 + x</c>.
    /// Do not mutate directly outside of engine internals.
    /// </summary>
    public readonly Piece[] Cells = new Piece[64];

    /// <summary>Returns the piece at column <paramref name="x"/>, row <paramref name="y"/>.</summary>
    public Piece Get(int x, int y) => Cells[y * 8 + x];

    /// <summary>Sets the piece at column <paramref name="x"/>, row <paramref name="y"/>.</summary>
    public void Set(int x, int y, Piece p) => Cells[y * 8 + x] = p;

    /// <summary>Returns a shallow copy of this board.</summary>
    public Board Clone()
    {
        var b = new Board();
        Cells.CopyTo(b.Cells, 0);
        return b;
    }

    /// <summary>
    /// Applies <paramref name="mv"/> and returns a <b>new</b> board.
    /// The current board is never modified.
    /// </summary>
    /// <param name="mv">The move to apply.</param>
    /// <param name="skipPromotion">
    /// When <c>true</c>, a pawn reaching the back rank is NOT promoted to a king.
    /// This is used during chain-capture look-ahead: Brazilian rules require promotion
    /// to happen only at the end of the entire capture sequence ("dama-voadora" rule).
    /// </param>
    public Board Apply(in Move mv, bool skipPromotion = false)
    {
        var nb    = Clone();
        var piece = nb.Get(mv.Fx, mv.Fy);
        nb.Set(mv.Fx, mv.Fy, Piece.Empty);

        if (!skipPromotion)
        {
            if (piece == Piece.BlackPawn && mv.Ty == 7) piece = Piece.BlackKing;
            if (piece == Piece.RedPawn   && mv.Ty == 0) piece = Piece.RedKing;
        }

        nb.Set(mv.Tx, mv.Ty, piece);

        // Remove all pieces along the capture diagonal
        if (mv.IsCapture)
        {
            int dx    = Math.Sign(mv.Tx - mv.Fx);
            int dy    = Math.Sign(mv.Ty - mv.Fy);
            int steps = Math.Abs(mv.Tx - mv.Fx);
            for (int i = 1; i < steps; i++)
                nb.Set(mv.Fx + dx * i, mv.Fy + dy * i, Piece.Empty);
        }

        return nb;
    }

    /// <summary>Counts pieces by type.</summary>
    /// <returns>Tuple (blackPawns, blackKings, redPawns, redKings).</returns>
    public (int bp, int bk, int rp, int rk) CountPieces()
    {
        int bp = 0, bk = 0, rp = 0, rk = 0;
        foreach (var p in Cells)
            switch (p)
            {
                case Piece.BlackPawn: bp++; break;
                case Piece.BlackKing: bk++; break;
                case Piece.RedPawn:   rp++; break;
                case Piece.RedKing:   rk++; break;
            }
        return (bp, bk, rp, rk);
    }

    // ─── Factories ────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="Board"/> from a JSON 8×8 array of strings.
    /// Each element is <c>"r"</c>, <c>"R"</c>, <c>"b"</c>, <c>"B"</c>, or <c>null</c>/absent.
    /// </summary>
    /// <param name="arr">8×8 JsonElement grid (row-major: arr[y][x]).</param>
    public static Board FromJson(JsonElement[][] arr)
    {
        var b = new Board();
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            string? s = arr[y][x].ValueKind == JsonValueKind.String
                ? arr[y][x].GetString()
                : null;
            b.Set(x, y, PieceHelper.FromString(s));
        }
        return b;
    }

    /// <summary>
    /// Creates a <see cref="Board"/> from a 2-D jagged string array (row-major).
    /// Accepted values: <c>"r"</c>, <c>"R"</c>, <c>"b"</c>, <c>"B"</c>, <c>null</c>.
    /// </summary>
    /// <param name="grid">8×8 array where grid[y][x] is the piece string (or null for empty).</param>
    public static Board FromArray(string?[][] grid)
    {
        var b = new Board();
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            b.Set(x, y, PieceHelper.FromString(grid[y][x]));
        return b;
    }

    /// <summary>
    /// Returns the standard starting position for Brazilian checkers.
    /// Red (human) occupies rows 5-7; Black (engine) occupies rows 0-2.
    /// Only dark squares are used (where (x + y) is odd).
    /// </summary>
    public static Board StartingPosition()
    {
        var b = new Board();
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            if ((x + y) % 2 == 0) continue; // light square, always empty
            if (y <= 2) b.Set(x, y, Piece.BlackPawn);
            if (y >= 5) b.Set(x, y, Piece.RedPawn);
        }
        return b;
    }

    /// <summary>
    /// Serializes the board to a 2-D jagged string array (row-major, 8×8).
    /// Empty squares are represented as <c>null</c>.
    /// </summary>
    public string?[][] ToArray()
    {
        var result = new string?[8][];
        for (int y = 0; y < 8; y++)
        {
            result[y] = new string?[8];
            for (int x = 0; x < 8; x++)
                result[y][x] = PieceHelper.ToString(Get(x, y));
        }
        return result;
    }
}
