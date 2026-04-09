# Architecture

Technical deep-dive into the Brazilian Checkers engine internals.

---

## Overview

The engine is a single .NET 8 class library (`CheckersEngine.dll`). There is no UI, no network code, and no platform-specific dependencies. Everything is pure managed C# with unsafe blocks disabled.

```
CheckersEngine/
├── BrazilianCheckersEngine.cs   ← Public façade (single entry point for consumers)
├── EngineConfig.cs              ← Immutable configuration record + presets
├── DrawAdvisor.cs               ← Heuristic draw accept/offer logic
├── GameMemory.cs                ← Repetition tracking + opponent profiling
└── Engine/
    ├── Piece.cs                 ← Piece enum (byte-sized) + PieceHelper
    ├── Move.cs                  ← Move value struct (fx,fy,tx,ty,isCapture)
    ├── Board.cs                 ← Board state, Apply, factories, serialization
    ├── MoveGenerator.cs         ← Legal move generator (max-capture rule)
    ├── Evaluator.cs             ← Static position evaluator
    ├── TranspositionTable.cs    ← Zobrist TT (always-replace)
    ├── Search.cs                ← PVS + iterative deepening + all heuristics
    └── Tablebase/
        ├── TablebaseEntry.cs    ← Outcome enum + result record
        ├── PositionIndex.cs     ← Combinatorial position encoding
        ├── EndgameTablebase.cs  ← Public API: generate, load, probe, BestMove
        └── TablebaseGenerator.cs← Retrograde analysis generator
```

---

## Board Representation

```
Board.Cells: Piece[64]   — flat array, index = y * 8 + x
```

- Cell (0,0) is top-left.
- Black advances from y = 0 toward y = 7; Red from y = 7 toward y = 0.
- Only dark squares are ever occupied: squares where `(x + y) % 2 == 1`.
- `Board.Apply()` returns a **new** board (copy-on-write); the original is never mutated.
- Allocation per search node: one 64-byte array clone per `Apply()` call.

### Piece encoding

| Value | Name       | Side  |
|------:|------------|-------|
|     0 | Empty      | —     |
|     1 | RedPawn    | Red   |
|     2 | RedKing    | Red   |
|     3 | BlackPawn  | Black |
|     4 | BlackKing  | Black |
|     5 | Sentinel   | —     |

`Sentinel` is a temporary marker placed on captured squares during king chain-capture depth calculation so the king cannot re-cross the same diagonal. It is never stored in persistent board state.

---

## Move Generation

`MoveGenerator.GetLegalMoves(board, blackTurn)` implements the full Brazilian checkers ruleset:

1. **Mandatory capture**: if any capture exists, only captures are returned.
2. **Maximum-capture rule** (*regra do maior número*): among all captures, only those belonging to the longest chain are returned. Chain length is computed recursively by `MaxCaptureDepth`.
3. **King sliding**: kings scan each diagonal until blocked; can capture an enemy on any square in a diagonal and land anywhere beyond.
4. **Dama-voadora**: a pawn reaching the promotion row mid-chain is NOT promoted until the entire chain ends.
5. **Anti-re-crossing**: kings use `Piece.Sentinel` markers during chain detection so they cannot capture across an already-jumped square.

---

## Search

### Entry point

`Search.FindBestMove(board, activePieceX, activePieceY, thinkingMs)`:

1. Checks the **opening book** (piece-count-gated, legal-move validated).
2. Probes the **endgame tablebase** at the root (if attached).
3. Runs **iterative deepening** from depth 1 to `MaxDepth = 60`, stopping when the deadline expires.
4. Uses **aspiration windows** for depths > 3 to narrow the initial α-β window.
5. Returns the best move found at the deepest fully-completed iteration (minimum `MinDepth`).

### PVS (Principal Variation Search)

Each node in `PVS(board, depth, α, β, blackTurn, deadline, ply)`:

```
1. Timeout check
2. Zobrist hash (includes side-to-move bit)
3. In-search repetition detection (return 0 if hash seen on current path)
4. Transposition table lookup
5. Endgame tablebase probe (if tablebase attached and depth > 0)
6. Generate legal moves
7. Terminal node: no moves → win/loss score
8. Leaf node: depth ≤ 0 → quiescence or static eval
9. Order moves (TT move → captures by MVV → killers → history)
10. First move: full [α, β] window
11. Subsequent moves: null window [α, α+1] (maximizer) or [β-1, β] (minimizer)
12. Re-search with full window on fail-high/fail-low
13. LMR: reduce depth for late quiet moves; re-search if result improves bound
14. Update killers + history on β-cutoffs
15. Store result in TT
```

### Score convention

All scores are from **black's perspective**:
- Positive → black (engine) is winning.
- Negative → red (human) is winning.
- `|score| ≥ WinBase (100 000)` → forced win/loss.

Win/loss scores encode remaining depth: `WinBase + depth × 10` so the engine prefers faster mates and delays losses.

### Aspiration windows

Initial half-width: `AspirationWindowInitial` (default 60 cp). On fail-high or fail-low, widened by 4× up to `AspirationWindowMax` (default 2000 cp), then retried. Falls back to infinite window if the maximum is exceeded.

### LMR (Late Move Reductions)

Applied to quiet (non-capture) moves when:
- Move index ≥ `LmrMinMoveIndex` (default 4)
- Remaining depth ≥ `LmrMinDepth` (default 3)

Reduction: 1 normally, 2 for moves at index ≥ `LmrAggressiveIndex` (default 8).

After a reduced-depth search the result is re-searched at full depth if it improves the bound.

### In-search repetition detection

A `ulong[]` stack (`_repPath`, max depth 128) tracks Zobrist hashes on the current search path. At the start of each PVS call the hash is checked against the stack; if already present, 0 (draw) is returned immediately. The hash is pushed before recursing and popped on exit.

This prevents the engine from cycling into repetitions during search.

---

## Transposition Table

- **Size**: power-of-2 entry count (default 2²¹ ≈ 2 M entries, ≈ 80 MB).
- **Hash**: Zobrist, 64-bit. Encodes piece positions AND side to move (via `TurnKey` XOR).
- **Policy**: always-replace — every new entry overwrites the slot unconditionally.
- **Entry layout** (`TtEntry` record struct, ≈ 40 bytes):

| Field    | Type    | Description                                     |
|----------|---------|-------------------------------------------------|
| Hash     | ulong   | Full 64-bit Zobrist key (for collision checking)|
| Depth    | int     | Search depth at which entry was stored          |
| Score    | int     | Score (black's perspective)                     |
| Flag     | TtFlag  | Exact / Lower bound / Upper bound               |
| BestMove | Move    | Best move found at this node (for move ordering)|

### TtFlag semantics

| Flag  | Node type | Usable when             |
|-------|-----------|-------------------------|
| Exact | PV node   | Always                  |
| Lower | Cut node  | `score ≥ β`             |
| Upper | All node  | `score ≤ α`             |

---

## Evaluator

`Evaluator.Evaluate(board)` returns a score from black's perspective in centipawns (cp).

### Components

| Component          | Description                                             | Weight       |
|--------------------|---------------------------------------------------------|--------------|
| Material           | Pawn = 100 cp, King = 400 cp                            | exact        |
| Piece-square tables| Pawn advancement + king centrality                      | 0–10 / 0–32  |
| Mobility           | Legal moves per side                                    | ×6 / ×10 EG  |
| Threats            | Pieces immediately capturable by opponent               | ×40 / ×60 EG |
| Backed pieces      | Piece with a diagonal friendly neighbor                 | +5           |
| Pre-promotion      | Pawn on the row before back rank                        | ±30          |
| Border penalty     | Edge-file pawn (middlegame only)                        | −5           |
| King pursuit       | Deep endgame: Chebyshev distance kings→enemy pieces     | ×8           |

Endgame threshold: `total ≤ 10`. Deep endgame: `total ≤ 6`.

**Known cost**: `Evaluate` calls `MoveGenerator.GetLegalMoves` twice (once per side) for mobility, making it the most expensive part of leaf evaluation.

---

## Endgame Tablebase

### Encoding (PositionIndex)

Positions are indexed using the **combinatorial number system** over the 32 dark squares of the board. This gives a perfectly compact enumeration with no wasted entries.

Index space for a `(bp, bk, rp, rk)` configuration:

```
C(32, bp) × C(32-bp, bk) × C(32-bp-bk, rp) × C(32-bp-bk-rp, rk)
```

### Storage

- `Dictionary<(bp,bk,rp,rk), (byte[] B, byte[] R)>` — one byte array per side.
- Each byte: `bits[7:6]` = outcome (Win/Loss/Draw/Unknown), `bits[5:0]` = DTM clamped to 63.

### Generation (retrograde analysis)

Three-phase BFS in `TablebaseGenerator`:

1. **Phase 1**: identify terminal positions (no legal moves = Loss for that side) and capture-transition wins/losses by probing already-solved sub-tablebases.
2. **Phase 2**: BFS backward through non-capture moves. A Win for the side to move at a child propagates a Win to predecessor positions that have a legal move there. A Loss propagates via a `rem[]` counter: when all successors are proven Losses the position becomes a Loss.
3. **Phase 3**: all remaining Unknown positions → Draw.

### Integration with search

The tablebase is probed at **two levels**:

1. **Root**: before iterative deepening begins in `FindBestMove`. If the position is covered, the tablebase's `BestMove()` is returned instantly.
2. **Inside PVS**: at every node with `depth > 0`, `Probe()` is called. On a hit, the exact score is stored in the TT and returned immediately, cutting off the subtree entirely.

---

## Draw Advisor

`DrawAdvisor` is a pure heuristic (no search). It runs fast O(n) analysis on the current board using:

- `MatScore`: material balance in cp.
- `BPawnsNearPromo`: black pawns at y ≥ 6.
- `BotCaptures`: whether black has any capture available.
- `BotThreats`: count of black pieces immediately capturable by red.
- `Reps`: repetition count from `GameMemory`.

Decision trees in `ShouldAcceptDraw` and `ShouldOfferDraw` map these metrics to a draw recommendation. Thresholds are configurable via `EngineConfig`.

---

## Opening Book

A small table of `(minTotal, maxTotal, Move)` triples. Entries are gated by total piece count, then validated against the actual legal moves. If the book move is not legal in the current position, the engine falls through to full search.

**All book moves use dark squares** (`(x + y) % 2 == 1`), matching the Brazilian checkers layout.

---

## Thread Safety

A single `BrazilianCheckersEngine` instance is **not** thread-safe. The `Search` object holds mutable state (TT, killers, history, repetition path). Create one engine instance per concurrent game.

`EndgameTablebase` is read-only after construction and **is** thread-safe for concurrent probing.
