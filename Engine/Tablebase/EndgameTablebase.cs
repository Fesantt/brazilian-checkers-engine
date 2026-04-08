namespace CheckersEngine.Engine.Tablebase;

/// <summary>
/// Endgame tablebase for Brazilian checkers (Damas Brasileiras).
/// </summary>
/// <remarks>
/// Generated via <b>retrograde analysis</b>: all positions with at most
/// <see cref="MaxPieces"/> total pieces are solved with perfect play from both sides.
///
/// <b>Quick start:</b>
/// <code>
/// // Generate once (CPU-intensive; ~seconds for ≤5 pieces, ~minutes for 6)
/// var tb = EndgameTablebase.Generate(maxPieces: 6, progress: new Progress&lt;GenerationProgress&gt;(
///     p =&gt; Console.WriteLine($"  {p.Config}: {p.Positions:N0} positions in {p.ElapsedMs} ms")
/// ));
/// tb.Save("tablebase/");
///
/// // Load (fast — a few seconds)
/// var tb = EndgameTablebase.Load("tablebase/");
///
/// // Probe a position
/// var result = tb.Probe(board, blackTurn: true);
/// // result?.Outcome == Win/Loss/Draw, result?.Dtm == plies to resolution
///
/// // Best move from the tablebase
/// Move? best = tb.BestMove(board, blackTurn: true);
///
/// // Integrate with the engine (tablebase is probed before full search)
/// var engine = new BrazilianCheckersEngine(tablebase: tb);
/// </code>
///
/// <b>Memory during generation (approximate peak):</b>
/// <list type="bullet">
///   <item>≤5 pieces: ~150 MB peak per configuration.</item>
///   <item>6 pieces: up to ~2 GB peak for the largest configurations.</item>
/// </list>
///
/// <b>Disk size:</b> 2 bytes per position per configuration.
/// </remarks>
public sealed class EndgameTablebase
{
    // Stored tables: (bp,bk,rp,rk) → (black-to-move byte array, red-to-move byte array)
    // One byte per position encodes outcome (2 bits) + DTM (6 bits, clamped to 63).
    private readonly Dictionary<(int, int, int, int), (byte[] B, byte[] R)> _tables = new();

    /// <summary>Maximum total piece count covered by this tablebase.</summary>
    public int MaxPieces { get; private set; }

    private EndgameTablebase() { }

    // ─── Factory methods ──────────────────────────────────────────────────────

    /// <summary>
    /// Generates the tablebase for all configurations with at most <paramref name="maxPieces"/>
    /// total pieces. This is a one-time CPU-intensive operation; save to disk afterwards.
    /// </summary>
    /// <param name="maxPieces">
    /// Maximum total piece count. Allowed range: 2–8. Default: 6.
    /// See the memory estimates in the class remarks before choosing 7+.
    /// </param>
    /// <param name="progress">
    /// Optional progress callback. Invoked once per completed configuration with statistics.
    /// </param>
    public static EndgameTablebase Generate(
        int maxPieces = 6,
        IProgress<GenerationProgress>? progress = null)
    {
        if (maxPieces < 2 || maxPieces > 8)
            throw new ArgumentOutOfRangeException(nameof(maxPieces), "Must be between 2 and 8.");

        var tb = new EndgameTablebase { MaxPieces = maxPieces };
        TablebaseGenerator.Generate(tb, maxPieces, progress);
        return tb;
    }

    /// <summary>
    /// Generates the tablebase for a single, specific piece configuration
    /// (e.g. 1 black king vs 2 red kings).
    /// </summary>
    /// <param name="bp">Black pawns.</param>
    /// <param name="bk">Black kings.</param>
    /// <param name="rp">Red pawns.</param>
    /// <param name="rk">Red kings.</param>
    /// <param name="progress">Optional progress callback.</param>
    /// <remarks>
    /// Sub-configurations with fewer pieces are generated automatically so that
    /// captures are handled correctly.
    /// </remarks>
    public static EndgameTablebase GenerateConfig(
        int bp, int bk, int rp, int rk,
        IProgress<GenerationProgress>? progress = null)
    {
        if (bp < 0 || bk < 0 || rp < 0 || rk < 0)
            throw new ArgumentOutOfRangeException("Piece counts must be non-negative.");
        if (bp + bk == 0 || rp + rk == 0)
            throw new ArgumentException("Each side must have at least one piece.");
        int total = bp + bk + rp + rk;
        if (total > 32)
            throw new ArgumentException("Total pieces cannot exceed 32.");

        // Generate all sub-configurations first (needed for capture lookups)
        var tb = new EndgameTablebase { MaxPieces = total };
        TablebaseGenerator.Generate(tb, total, progress);
        return tb;
    }

    /// <summary>
    /// Loads a previously generated tablebase from <paramref name="directory"/>.
    /// </summary>
    public static EndgameTablebase Load(string directory)
    {
        var tb = new EndgameTablebase();
        tb.LoadDirectory(directory);
        return tb;
    }

    // ─── Probe ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Probes the tablebase for the given board position.
    /// </summary>
    /// <param name="board">The position to probe.</param>
    /// <param name="blackTurn"><c>true</c> if it is black's turn to move.</param>
    /// <returns>
    /// The result from the side-to-move's perspective, or <c>null</c> if the position
    /// has too many pieces and is not covered.
    /// </returns>
    public TablebaseResult? Probe(Board board, bool blackTurn)
    {
        var (bp, bk, rp, rk) = CountPieces(board);
        var r = ProbeConfig(bp, bk, rp, rk, board, blackTurn);
        return r.IsKnown ? r : null;
    }

    /// <summary>
    /// Returns the tablebase-optimal move from the given position, or <c>null</c> if
    /// the position is not covered.
    /// </summary>
    /// <remarks>
    /// Move ranking (best first):
    /// <list type="bullet">
    ///   <item>Any Win (prefers smallest opponent DTM = fastest win).</item>
    ///   <item>Draw (if no win is available).</item>
    ///   <item>Loss (if forced; prefers largest opponent DTM = longest survival).</item>
    /// </list>
    /// </remarks>
    public Move? BestMove(Board board, bool blackTurn)
    {
        var (bp, bk, rp, rk) = CountPieces(board);
        if (!_tables.ContainsKey((bp, bk, rp, rk))) return null;

        var moves = MoveGenerator.GetLegalMoves(board, blackTurn);
        if (moves.Count == 0) return null;

        Move? best      = null;
        int   bestScore = int.MinValue;

        foreach (var mv in moves)
        {
            var next = board.Apply(mv);
            var r    = Probe(next, !blackTurn);
            if (r == null) continue;

            // Convert opponent's result to our score:
            //   Opponent Loss → our Win:  score = +10000 - dtm (prefer faster wins)
            //   Opponent Draw → score = 0
            //   Opponent Win  → our Loss: score = −10000 + dtm (prefer slower losses)
            int score = r.Value.Outcome switch
            {
                TablebaseOutcome.Loss => 10_000 - r.Value.Dtm,
                TablebaseOutcome.Draw => 0,
                TablebaseOutcome.Win  => -10_000 + r.Value.Dtm,
                _                     => -20_000,
            };

            if (score > bestScore) { bestScore = score; best = mv; }
        }

        return best;
    }

    // ─── Persistence ──────────────────────────────────────────────────────────

    /// <summary>
    /// Saves all tablebase data to binary <c>.tb</c> files in <paramref name="directory"/>.
    /// Each configuration produces one file named <c>{bp}_{bk}_{rp}_{rk}.tb</c>.
    /// </summary>
    public void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "meta.txt"), $"maxPieces={MaxPieces}");

        foreach (var ((bp, bk, rp, rk), (tB, tR)) in _tables)
        {
            string path = Path.Combine(directory, $"{bp}_{bk}_{rp}_{rk}.tb");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            using var bw = new BinaryWriter(fs);
            bw.Write(tB.Length); // int32 — both tables have the same length
            bw.Write(tB);
            bw.Write(tR);
        }
    }

    private void LoadDirectory(string directory)
    {
        string meta = Path.Combine(directory, "meta.txt");
        if (File.Exists(meta))
        {
            var line = File.ReadAllText(meta).Trim();
            if (line.StartsWith("maxPieces=") && int.TryParse(line[10..], out int mp))
                MaxPieces = mp;
        }

        foreach (string file in Directory.GetFiles(directory, "*.tb"))
        {
            var parts = Path.GetFileNameWithoutExtension(file).Split('_');
            if (parts.Length != 4) continue;
            if (!int.TryParse(parts[0], out int bp) || !int.TryParse(parts[1], out int bk) ||
                !int.TryParse(parts[2], out int rp) || !int.TryParse(parts[3], out int rk)) continue;

            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var br = new BinaryReader(fs);
            int   len = br.ReadInt32();
            byte[] tB = br.ReadBytes(len);
            byte[] tR = br.ReadBytes(len);
            _tables[(bp, bk, rp, rk)] = (tB, tR);
        }
    }

    // ─── Information ──────────────────────────────────────────────────────────

    /// <summary>Number of piece configurations loaded in this tablebase.</summary>
    public int ConfigCount => _tables.Count;

    /// <summary>Total number of position entries (each side counts separately).</summary>
    public long TotalPositions => _tables.Values.Sum(t => (long)t.B.Length * 2);

    /// <summary>Approximate on-disk size in bytes.</summary>
    public long DiskBytes => _tables.Values.Sum(t => (long)t.B.Length * 2 + 4);

    /// <summary>
    /// Returns one summary line per configuration, sorted by piece count.
    /// </summary>
    public IEnumerable<string> ConfigSummary() =>
        _tables
            .OrderBy(kv => kv.Key.Item1 + kv.Key.Item2 + kv.Key.Item3 + kv.Key.Item4)
            .ThenBy(kv => kv.Key)
            .Select(kv =>
            {
                var (bp, bk, rp, rk) = kv.Key;
                return $"{bp}bp+{bk}bk vs {rp}rp+{rk}rk  ({kv.Value.B.Length:N0} positions/side)";
            });

    // ─── Internal ─────────────────────────────────────────────────────────────

    internal TablebaseResult ProbeConfig(int bp, int bk, int rp, int rk, Board board, bool blackTurn)
    {
        // A side with no pieces loses immediately (handles the "last piece captured" case)
        if (blackTurn  && bp + bk == 0) return new TablebaseResult(TablebaseOutcome.Loss, 0);
        if (!blackTurn && rp + rk == 0) return new TablebaseResult(TablebaseOutcome.Loss, 0);

        if (!_tables.TryGetValue((bp, bk, rp, rk), out var tables))
            return TablebaseResult.UnknownResult;

        long idx = PositionIndex.Encode(board, bp, bk, rp, rk);
        if (idx < 0) return TablebaseResult.UnknownResult;

        return TablebaseResult.Unpack((blackTurn ? tables.B : tables.R)[idx]);
    }

    internal void StoreConfig(int bp, int bk, int rp, int rk, byte[] tableB, byte[] tableR)
        => _tables[(bp, bk, rp, rk)] = (tableB, tableR);

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
