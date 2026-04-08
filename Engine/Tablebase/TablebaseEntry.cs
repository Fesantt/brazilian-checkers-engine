namespace CheckersEngine.Engine.Tablebase;

/// <summary>Outcome of a tablebase probe from the side-to-move's perspective.</summary>
public enum TablebaseOutcome : byte
{
    /// <summary>Position not covered by the tablebase (too many pieces).</summary>
    Unknown = 0,
    /// <summary>Side to move wins with perfect play.</summary>
    Win     = 1,
    /// <summary>Side to move loses with perfect play from both sides.</summary>
    Loss    = 2,
    /// <summary>Neither side can force a win — theoretical draw.</summary>
    Draw    = 3,
}

/// <summary>
/// Result of a tablebase probe.
/// </summary>
/// <param name="Outcome">Win / Loss / Draw from the side-to-move's perspective.</param>
/// <param name="Dtm">
/// Distance-to-mate in plies (0 = immediate loss with 0 moves available).
/// Meaningful only for <see cref="TablebaseOutcome.Win"/> and <see cref="TablebaseOutcome.Loss"/>.
/// Clamped to 63 during generation.
/// </param>
public readonly record struct TablebaseResult(TablebaseOutcome Outcome, int Dtm)
{
    /// <summary>Whether the result is conclusively known (not <see cref="TablebaseOutcome.Unknown"/>).</summary>
    public bool IsKnown => Outcome != TablebaseOutcome.Unknown;

    /// <summary>Human-readable description of the result.</summary>
    public override string ToString()
    {
        int moves = (Dtm + 1) / 2;
        return Outcome switch
        {
            TablebaseOutcome.Win  => $"Win in {moves} move{(moves != 1 ? "s" : "")}",
            TablebaseOutcome.Loss => $"Loss in {moves} move{(moves != 1 ? "s" : "")}",
            TablebaseOutcome.Draw => "Draw",
            _                     => "Unknown",
        };
    }

    // ─── Binary packing ───────────────────────────────────────────────────────

    // 1 byte: bits [7:6] = outcome (0-3), bits [5:0] = DTM clamped to 63
    internal byte Pack() => (byte)(((byte)Outcome << 6) | (byte)Math.Min(Dtm, 63));

    internal static TablebaseResult Unpack(byte b) =>
        new((TablebaseOutcome)(b >> 6), b & 0x3F);

    internal static readonly TablebaseResult UnknownResult = new(TablebaseOutcome.Unknown, 0);
}
