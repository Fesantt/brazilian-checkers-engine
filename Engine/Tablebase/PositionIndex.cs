namespace CheckersEngine.Engine.Tablebase;

/// <summary>
/// Maps board positions with a fixed piece configuration to and from compact integer indices.
/// </summary>
/// <remarks>
/// Uses the <b>combinatorial number system</b> (colexicographic order) to achieve a
/// minimal perfect hash with no gaps.
/// <para>
/// The 32 dark squares of the 8×8 board are numbered 0–31 in reading order (left→right, top→bottom).
/// For a configuration (bp, bk, rp, rk) the index space has size:
/// <c>C(32,bp) × C(32−bp,bk) × C(32−bp−bk,rp) × C(32−bp−bk−rp,rk)</c>.
/// </para>
/// </remarks>
internal static class PositionIndex
{
    /// <summary>The 32 dark squares of the 8×8 board, numbered 0–31 in reading order.</summary>
    internal static readonly (int x, int y)[] Squares = new (int, int)[32];

    /// <summary>Inverse lookup: board (x,y) → dark-square index, or −1 for light squares.</summary>
    private static readonly int[,] _sqIdx = new int[8, 8];

    /// <summary>Pascal's triangle: C[n][k] for n,k ∈ [0, 33].</summary>
    private static readonly long[,] C = new long[34, 34];

    static PositionIndex()
    {
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            _sqIdx[x, y] = -1;

        int s = 0;
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            if ((x + y) % 2 == 1)
            {
                Squares[s]    = (x, y);
                _sqIdx[x, y]  = s++;
            }

        C[0, 0] = 1;
        for (int n = 1; n <= 33; n++)
        {
            C[n, 0] = 1;
            for (int k = 1; k <= n; k++)
                C[n, k] = C[n - 1, k - 1] + C[n - 1, k];
        }
    }

    // ─── Public API ───────────────────────────────────────────────────────────

    /// <summary>Total number of distinct positions for the given piece configuration.</summary>
    public static long ConfigSize(int bp, int bk, int rp, int rk)
    {
        if (bp + bk + rp + rk > 32) return 0;
        return C[32, bp] * C[32 - bp, bk] * C[32 - bp - bk, rp] * C[32 - bp - bk - rp, rk];
    }

    /// <summary>
    /// Encodes a board position into a compact integer index for the given configuration.
    /// Returns −1 if the board's piece counts do not match the expected configuration.
    /// </summary>
    public static long Encode(Board board, int bp, int bk, int rp, int rk)
    {
        // Collect sorted dark-square indices for each piece type (squares are visited 0→31)
        Span<int> bpSq = stackalloc int[Math.Max(bp, 1)];
        Span<int> bkSq = stackalloc int[Math.Max(bk, 1)];
        Span<int> rpSq = stackalloc int[Math.Max(rp, 1)];
        Span<int> rkSq = stackalloc int[Math.Max(rk, 1)];
        int bpN = 0, bkN = 0, rpN = 0, rkN = 0;

        for (int sq = 0; sq < 32; sq++)
        {
            var (x, y) = Squares[sq];
            switch (board.Get(x, y))
            {
                case Piece.BlackPawn: if (bpN < bp) bpSq[bpN++] = sq; else return -1; break;
                case Piece.BlackKing: if (bkN < bk) bkSq[bkN++] = sq; else return -1; break;
                case Piece.RedPawn:   if (rpN < rp) rpSq[rpN++] = sq; else return -1; break;
                case Piece.RedKing:   if (rkN < rk) rkSq[rkN++] = sq; else return -1; break;
            }
        }
        if (bpN != bp || bkN != bk || rpN != rp || rkN != rk) return -1;

        // Step 1: Black pawns among all 32 dark squares
        long iBp = CombIdx(bpSq[..bp]);

        // Step 2: Black kings among the (32−bp) squares not occupied by black pawns
        Span<int> avail1 = stackalloc int[32 - bp];
        int n1 = SubtractRange(avail1, 32, bpSq[..bp]);
        Span<int> bkRel = stackalloc int[Math.Max(bk, 1)];
        ToRelative(bkSq[..bk], avail1[..n1], bkRel);
        long iBk = CombIdx(bkRel[..bk]);

        // Step 3: Red pawns among the remaining (32−bp−bk) squares
        Span<int> avail2 = stackalloc int[32 - bp - bk];
        int n2 = SubtractSpan(avail2, avail1[..n1], bkSq[..bk]);
        Span<int> rpRel = stackalloc int[Math.Max(rp, 1)];
        ToRelative(rpSq[..rp], avail2[..n2], rpRel);
        long iRp = CombIdx(rpRel[..rp]);

        // Step 4: Red kings among the remaining (32−bp−bk−rp) squares
        Span<int> avail3 = stackalloc int[32 - bp - bk - rp];
        int n3 = SubtractSpan(avail3, avail2[..n2], rpSq[..rp]);
        Span<int> rkRel = stackalloc int[Math.Max(rk, 1)];
        ToRelative(rkSq[..rk], avail3[..n3], rkRel);
        long iRk = CombIdx(rkRel[..rk]);

        long s3 = C[32 - bp - bk - rp, rk];
        long s2 = C[32 - bp - bk, rp] * s3;
        long s1 = C[32 - bp, bk] * s2;
        return iBp * s1 + iBk * s2 + iRp * s3 + iRk;
    }

    /// <summary>
    /// Decodes a compact index back to a board position for the given configuration.
    /// </summary>
    public static Board Decode(long index, int bp, int bk, int rp, int rk)
    {
        long s3 = C[32 - bp - bk - rp, rk];
        long s2 = C[32 - bp - bk, rp] * s3;
        long s1 = C[32 - bp, bk] * s2;

        long iBp = index / s1;  index %= s1;
        long iBk = index / s2;  index %= s2;
        long iRp = index / s3;
        long iRk = index % s3;

        Span<int> bpSq = stackalloc int[Math.Max(bp, 1)];
        CombFromIdx(iBp, bp, 32, bpSq[..bp]);

        Span<int> avail1 = stackalloc int[32 - bp];
        int n1 = SubtractRange(avail1, 32, bpSq[..bp]);
        Span<int> bkRel = stackalloc int[Math.Max(bk, 1)];
        CombFromIdx(iBk, bk, n1, bkRel[..bk]);
        Span<int> bkSq = stackalloc int[Math.Max(bk, 1)];
        FromRelative(bkRel[..bk], avail1[..n1], bkSq[..bk]);

        Span<int> avail2 = stackalloc int[32 - bp - bk];
        int n2 = SubtractSpan(avail2, avail1[..n1], bkSq[..bk]);
        Span<int> rpRel = stackalloc int[Math.Max(rp, 1)];
        CombFromIdx(iRp, rp, n2, rpRel[..rp]);
        Span<int> rpSq = stackalloc int[Math.Max(rp, 1)];
        FromRelative(rpRel[..rp], avail2[..n2], rpSq[..rp]);

        Span<int> avail3 = stackalloc int[32 - bp - bk - rp];
        int n3 = SubtractSpan(avail3, avail2[..n2], rpSq[..rp]);
        Span<int> rkRel = stackalloc int[Math.Max(rk, 1)];
        CombFromIdx(iRk, rk, n3, rkRel[..rk]);
        Span<int> rkSq = stackalloc int[Math.Max(rk, 1)];
        FromRelative(rkRel[..rk], avail3[..n3], rkSq[..rk]);

        var board = new Board();
        foreach (int sq in bpSq[..bp]) { var (x,y) = Squares[sq]; board.Set(x, y, Piece.BlackPawn); }
        foreach (int sq in bkSq[..bk]) { var (x,y) = Squares[sq]; board.Set(x, y, Piece.BlackKing); }
        foreach (int sq in rpSq[..rp]) { var (x,y) = Squares[sq]; board.Set(x, y, Piece.RedPawn);   }
        foreach (int sq in rkSq[..rk]) { var (x,y) = Squares[sq]; board.Set(x, y, Piece.RedKing);   }
        return board;
    }

    // ─── Combinatorial number system ──────────────────────────────────────────

    // Colexicographic index of a sorted subset: sum C(subset[i], i+1)
    private static long CombIdx(ReadOnlySpan<int> subset)
    {
        long idx = 0;
        for (int i = 0; i < subset.Length; i++)
            idx += C[subset[i], i + 1];
        return idx;
    }

    // Recover sorted k-subset of {0..n-1} from its combinatorial index
    private static void CombFromIdx(long index, int k, int n, Span<int> result)
    {
        for (int i = k - 1; i >= 0; i--)
        {
            int v = i;
            while (v + 1 < n && C[v + 1, i + 1] <= index) v++;
            result[i] = v;
            index -= C[v, i + 1];
        }
    }

    // Fill avail[] with {0..n-1} minus elements in used[]
    private static int SubtractRange(Span<int> avail, int n, ReadOnlySpan<int> used)
    {
        int count = 0;
        for (int s = 0; s < n; s++)
        {
            bool skip = false;
            foreach (int u in used) if (u == s) { skip = true; break; }
            if (!skip) avail[count++] = s;
        }
        return count;
    }

    // Fill avail[] with elements of source[] that are NOT in used[]
    private static int SubtractSpan(Span<int> avail, ReadOnlySpan<int> source, ReadOnlySpan<int> used)
    {
        int count = 0;
        foreach (int s in source)
        {
            bool skip = false;
            foreach (int u in used) if (u == s) { skip = true; break; }
            if (!skip) avail[count++] = s;
        }
        return count;
    }

    // Map absolute dark-square indices to relative indices within available[]
    private static void ToRelative(ReadOnlySpan<int> chosen, ReadOnlySpan<int> avail, Span<int> result)
    {
        for (int i = 0; i < chosen.Length; i++)
            for (int j = 0; j < avail.Length; j++)
                if (avail[j] == chosen[i]) { result[i] = j; break; }
    }

    // Map relative indices back to absolute dark-square indices using available[]
    private static void FromRelative(ReadOnlySpan<int> relative, ReadOnlySpan<int> avail, Span<int> result)
    {
        for (int i = 0; i < relative.Length; i++)
            result[i] = avail[relative[i]];
    }
}
