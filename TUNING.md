# Tuning Guide

How to configure the engine for different use cases and how to improve it further.

---

## Built-in Presets

| Preset   | Think time | Min depth | TT size | Recommended for                        |
|----------|-----------|-----------|---------|----------------------------------------|
| `Blitz`  | 1 s       | 3         | ~20 MB  | Stress-testing, rapid prototyping      |
| `Fast`   | 2 s       | 5         | ~40 MB  | Casual games, mobile                   |
| `Default`| 8 s       | 9         | ~80 MB  | Standard competitive play              |
| `Strong` | 15 s      | 12        | ~160 MB | Analysis, correspondence, tournaments  |

---

## Configuration Reference

All fields on `EngineConfig` are `init`-only. Use `with` expressions to override:

```csharp
var cfg = EngineConfig.Default with
{
    ThinkingMs = 5_000,
    UseOpeningBook = false,
    TranspositionTableSizePow2 = 22,  // ~160 MB
};
var engine = new BrazilianCheckersEngine(cfg);
```

### Time budget (`ThinkingMs`)

The most important knob. The engine uses iterative deepening and will always return the best result found within the budget. Increasing time directly increases depth, which is the primary strength driver.

Practical depth–time relationship (approximate, varies by position):

| Budget  | Typical depth reached |
|---------|----------------------|
| 1 s     | 10–14                |
| 3 s     | 14–18                |
| 8 s     | 17–22                |
| 15 s    | 20–26                |
| 30 s    | 23–30                |

### Minimum depth (`MinDepth`)

The engine only commits to a result once this depth is completed. Increasing `MinDepth` reduces the risk of returning a shallow result on a fast machine, but can cause the engine to *not* return its deepest result if the deadline fires before `MinDepth` finishes.

**Rule of thumb**: keep `MinDepth` at 30–50% of the expected depth for the given time budget.

### Transposition table size (`TranspositionTableSizePow2`)

The TT is the engine's "memory" — larger means fewer hash collisions and better reuse across iterations. The improvement from doubling the TT is roughly equivalent to +1–2 depth.

Each entry is ≈ 40 bytes:

| Pow2 | Entries     | Memory  |
|------|-------------|---------|
| 19   | 524 288     | ~20 MB  |
| 20   | 1 048 576   | ~40 MB  |
| 21   | 2 097 152   | ~80 MB  |
| 22   | 4 194 304   | ~160 MB |
| 23   | 8 388 608   | ~320 MB |
| 24   | 16 777 216  | ~640 MB |

### LMR parameters

Late Move Reductions skip deep searches for moves that are probably weak. Aggressive LMR saves time but increases the chance of missing a good late move.

| Parameter            | Default | Effect of increasing         |
|----------------------|---------|------------------------------|
| `LmrMinMoveIndex`    | 4       | Start reducing later → safer |
| `LmrMinDepth`        | 3       | Only reduce deeper → safer   |
| `LmrAggressiveIndex` | 8       | Reduce by 2 later → safer    |

Reducing `LmrMinMoveIndex` to 2–3 and `LmrAggressiveIndex` to 5–6 increases speed at the cost of some accuracy — useful for blitz.

### Aspiration windows

The initial window width `AspirationWindowInitial` (default 60 cp) controls how tightly the search starts. If the true score is within the initial window, the search converges faster. If not, the engine widens and retries.

- Lower initial width (e.g., 30 cp) → faster convergence when score is stable, but more retries in sharp positions.
- Higher initial width (e.g., 100 cp) → fewer retries in sharp positions, more work overall.

### Draw thresholds

| Config field             | Default | Meaning                                     |
|--------------------------|---------|---------------------------------------------|
| `DrawRefuseAboveScore`   | 200 cp  | Refuse draws when engine is ahead by this much |
| `DrawAcceptBelowScore`   | −200 cp | Accept draws when engine is behind by this much |
| `DrawRepetitionThreshold`| 2       | Accept draw on this many repetitions in equal positions |

---

## Endgame Tablebase

The tablebase provides perfect play for positions with few pieces. It is probed both at the root and **inside** the search tree (at every PVS node), so it eliminates entire endgame subtrees.

### Generation cost

```csharp
var tb = EndgameTablebase.Generate(maxPieces: 6, progress: new Progress<GenerationProgress>(
    p => Console.WriteLine($"  {p.Config}: {p.Positions:N0} positions in {p.ElapsedMs} ms")
));
tb.Save("tablebase/");
```

| Max pieces | Approx. time | Peak RAM  | Disk size |
|------------|-------------|-----------|-----------|
| 4          | < 1 s       | < 50 MB   | < 1 MB    |
| 5          | 5–30 s      | ~150 MB   | ~5 MB     |
| 6          | 1–10 min    | ~2 GB     | ~50 MB    |

### Loading

```csharp
var tb     = EndgameTablebase.Load("tablebase/");
var engine = new BrazilianCheckersEngine(tablebase: tb);
```

Loading is fast (seconds) and consumes only the stored data (≈ 2 bytes/position).

---

## Strength Tips

### Use a tablebase

A 6-piece tablebase eliminates all endgame uncertainty. This is the single biggest strength improvement once the search is fast enough for the opening and middlegame.

### Increase think time

Going from 8 s to 15 s is roughly +2–4 depth and noticeably stronger. The engine scales well with time.

### Use Strong preset

`EngineConfig.Strong` with a 6-piece tablebase is the recommended configuration for maximum strength.

```csharp
var tb     = EndgameTablebase.Load("tablebase/");
var engine = new BrazilianCheckersEngine(EngineConfig.Strong, tb);
```

### Avoid Blitz for production

`EngineConfig.Blitz` reaches only depth 3 minimum, which is enough to avoid blunders but not strong enough for competitive play. Use `Default` or `Strong`.

---

## Known Limitations

| Area | Description |
|------|-------------|
| Board representation | Flat `Piece[64]` array with `Clone()` on every node. A bitboard representation would reduce GC pressure and improve throughput. |
| Evaluator mobility cost | `Evaluate` calls `MoveGenerator.GetLegalMoves` twice (once per side) at every leaf. This is the single most expensive line in the search. Incremental mobility would speed up evaluation significantly. |
| No null-move pruning | Standard chess technique not yet implemented. Could add 20–40% speed. |
| Always-replace TT | Two-bucket TT (depth-preferred + always-replace) would improve hit rates at deeper searches. |
| Opening book size | Current book covers only the first few moves. A larger position-keyed book (using full Zobrist hash) would improve opening quality. |
