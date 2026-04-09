namespace CheckersEngine.Engine;

/// <summary>Transposition table entry flag indicating the type of bound stored.</summary>
public enum TtFlag : byte
{
    /// <summary>Exact score — returned from a PV node.</summary>
    Exact,
    /// <summary>Lower bound — score caused a beta cut-off.</summary>
    Lower,
    /// <summary>Upper bound — score was below alpha (all-node).</summary>
    Upper,
}

/// <summary>A single entry in the transposition table.</summary>
public record struct TtEntry(ulong Hash, int Depth, int Score, TtFlag Flag, Move BestMove);

/// <summary>
/// Fixed-size transposition table using Zobrist hashing with an always-replace policy.
/// </summary>
/// <remarks>
/// <b>Design choices:</b>
/// <list type="bullet">
///   <item>
///     Size is a power of 2 so index lookup is a fast bitwise AND instead of modulo.
///     Default size (2^21 = 2 097 152 entries) uses ≈ 64 MB of memory.
///   </item>
///   <item>
///     Always-replace: every new entry overwrites the existing one at the same slot,
///     regardless of depth. This is simple and works well for iterative deepening.
///   </item>
///   <item>
///     Zobrist keys are seeded with a fixed value for reproducible results across runs.
///   </item>
/// </list>
/// </remarks>
public sealed class TranspositionTable
{
    private readonly int    _mask;
    private readonly TtEntry[] _table;

    // Pre-computed Zobrist keys: [squareIndex * 5 + (pieceValue - 1)]
    // Piece values: RedPawn=1, RedKing=2, BlackPawn=3, BlackKing=4
    private static readonly ulong[] ZKeys;

    /// <summary>
    /// XOR-ed into the hash when it is black's turn to move.
    /// Prevents positions with identical piece layouts but different sides-to-move
    /// from colliding in the transposition table.
    /// </summary>
    private static readonly ulong TurnKey;

    static TranspositionTable()
    {
        ZKeys = new ulong[64 * 5];
        var rng = new Random(unchecked((int)0xDEAD_BEEF));
        var buf = new byte[8];
        for (int i = 0; i < ZKeys.Length; i++)
        {
            rng.NextBytes(buf);
            ZKeys[i] = BitConverter.ToUInt64(buf);
        }
        rng.NextBytes(buf);
        TurnKey = BitConverter.ToUInt64(buf);
    }

    /// <summary>
    /// Creates a new transposition table.
    /// </summary>
    /// <param name="sizePow2">
    /// Entry count = 2^<paramref name="sizePow2"/>.
    /// Must be in [16, 24]. Defaults to 21 (≈ 64 MB).
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="sizePow2"/> is out of range.</exception>
    public TranspositionTable(int sizePow2 = 21)
    {
        if (sizePow2 < 16 || sizePow2 > 24)
            throw new ArgumentOutOfRangeException(nameof(sizePow2), "Must be in [16, 24].");
        int size = 1 << sizePow2;
        _mask  = size - 1;
        _table = new TtEntry[size];
    }

    /// <summary>
    /// Computes the Zobrist hash for <paramref name="board"/> and the side to move.
    /// The hash encodes both piece positions and whose turn it is, preventing
    /// collisions between positions with identical layouts but different active sides.
    /// </summary>
    /// <param name="board">The board position to hash.</param>
    /// <param name="blackTurn"><c>true</c> if it is black's turn to move.</param>
    public static ulong Hash(Board board, bool blackTurn)
    {
        ulong h = 0;
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
        {
            var p = board.Get(x, y);
            if (p == Piece.Empty || p == Piece.Sentinel) continue;
            h ^= ZKeys[(y * 8 + x) * 5 + (int)p - 1];
        }
        if (blackTurn) h ^= TurnKey;
        return h;
    }

    /// <summary>Stores an entry (always-replace policy).</summary>
    public void Store(ulong hash, int depth, int score, TtFlag flag, in Move best)
    {
        int idx = (int)(hash & (uint)_mask);
        _table[idx] = new TtEntry(hash, depth, score, flag, best);
    }

    /// <summary>
    /// Retrieves a usable score from the table.
    /// Returns <c>true</c> and sets <paramref name="score"/> if the entry is a hit
    /// with sufficient depth and a usable bound.
    /// </summary>
    public bool TryGet(ulong hash, int depth, int alpha, int beta,
                       out int score, out Move bestMove)
    {
        score    = 0;
        bestMove = default;
        ref var e = ref _table[hash & (uint)_mask];
        if (e.Hash != hash || e.Depth < depth) return false;
        bestMove = e.BestMove;
        if (e.Flag == TtFlag.Exact)                     { score = e.Score; return true; }
        if (e.Flag == TtFlag.Lower && e.Score >= beta)  { score = e.Score; return true; }
        if (e.Flag == TtFlag.Upper && e.Score <= alpha) { score = e.Score; return true; }
        return false;
    }

    /// <summary>
    /// Returns the cached best move for move ordering, regardless of depth.
    /// Does not validate bounds — use only for move ordering, not score retrieval.
    /// </summary>
    public bool TryGetMove(ulong hash, out Move bestMove)
    {
        ref var e = ref _table[hash & (uint)_mask];
        bestMove  = e.BestMove;
        return e.Hash == hash;
    }

    /// <summary>Clears all entries.</summary>
    public void Clear() => Array.Clear(_table);
}
