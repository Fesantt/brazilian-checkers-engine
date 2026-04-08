namespace CheckersEngine.Engine;

/// <summary>
/// Represents a single move (or one step in a capture chain) on the 8×8 board.
/// Stored as a lightweight value type to avoid heap pressure during search.
/// </summary>
/// <remarks>
/// Coordinates follow the (x, y) convention where x ∈ [0, 7] is the column
/// and y ∈ [0, 7] is the row, with (0, 0) at the top-left corner.
/// The black side advances from y=0 toward y=7; red from y=7 toward y=0.
/// </remarks>
public readonly struct Move(int fx, int fy, int tx, int ty, bool isCapture = false)
{
    /// <summary>Origin column.</summary>
    public readonly int Fx = fx;

    /// <summary>Origin row.</summary>
    public readonly int Fy = fy;

    /// <summary>Destination column.</summary>
    public readonly int Tx = tx;

    /// <summary>Destination row.</summary>
    public readonly int Ty = ty;

    /// <summary>
    /// <c>true</c> if this move captures at least one enemy piece.
    /// For king captures, the captured piece may be several squares away from
    /// the landing square — all squares between origin and destination are scanned.
    /// </summary>
    public readonly bool IsCapture = isCapture;

    /// <summary>Value-equality on all four coordinates (ignores <see cref="IsCapture"/>).</summary>
    public bool Equals(Move other) =>
        Fx == other.Fx && Fy == other.Fy && Tx == other.Tx && Ty == other.Ty;

    /// <inheritdoc/>
    public override string ToString() =>
        $"({Fx},{Fy})->({Tx},{Ty}){(IsCapture ? "x" : "")}";
}
