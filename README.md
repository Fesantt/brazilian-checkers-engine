# CheckersEngine

Brazilian Checkers (Damas Brasileiras) engine — pure C# class library targeting .NET 8.

---

## Features

| Feature | Description |
|---|---|
| **PVS / Negascout** | Principal Variation Search with null-window re-searches |
| **Iterative deepening** | Depths 1–60, time-bounded; best result always returned |
| **Aspiration windows** | Narrow α-β window around previous score for faster convergence |
| **Transposition table** | Zobrist hashing, configurable size (default ≈ 64 MB, always-replace) |
| **Quiescence search** | Resolves captures at leaf nodes with MVV ordering |
| **Killer moves** | 2 quiet-move slots per ply, carried across iterations |
| **History heuristic** | Depth²-weighted bonus for quiet moves that caused cut-offs |
| **LMR** | Late Move Reductions for late-ordered quiet moves |
| **Opening book** | 8 standard Brazilian checkers openings (instant response) |
| **Draw advisor** | Heuristic accept/offer logic with position and repetition analysis |
| **Opponent profiling** | Tracks human move statistics across games |

---

## Installation

This is a class library — reference it directly from your project:

```xml
<!-- In your .csproj -->
<ItemGroup>
  <ProjectReference Include="..\checkers-engine\CheckersEngine.csproj" />
</ItemGroup>
```

Requires **.NET 8.0** or later.

---

## Quick Start

```csharp
using CheckersEngine;
using CheckersEngine.Engine;

// Create engine with default settings (8 s per move)
var engine = new BrazilianCheckersEngine();

// Standard starting position
var board = Board.StartingPosition();

// Find best move for black (engine side)
Move? best = engine.FindBestMove(board);
if (best.HasValue)
{
    Console.WriteLine($"Engine plays: {best.Value}");
    board = Board.ApplyMove(board, best.Value); // or engine.ApplyMove(...)
}

// Check if the game is over
if (BrazilianCheckersEngine.IsGameOver(board, blackTurn: false))
    Console.WriteLine("Human has no moves — engine wins!");
```

---

## Configuration

All settings live in `EngineConfig`, an immutable record with `init`-only properties.

### Presets

```csharp
EngineConfig.Blitz   // 1 s,  depth ≥ 3,  small TT (~16 MB)
EngineConfig.Fast    // 2 s,  depth ≥ 5,  medium TT (~32 MB)
EngineConfig.Default // 8 s,  depth ≥ 9,  standard TT (~64 MB)  ← matches services/checkers_bot
EngineConfig.Strong  // 15 s, depth ≥ 12, large TT (~128 MB)
```

### Custom configuration

Use C# `with` expressions to override individual properties:

```csharp
var cfg = EngineConfig.Default with
{
    ThinkingMs = 5_000,       // 5 seconds per move
    MinDepth   = 7,           // accept result after depth 7
    UseOpeningBook = false,   // disable book (for testing)
};
var engine = new BrazilianCheckersEngine(cfg);
```

### All options

#### Time

| Property | Default | Description |
|---|---|---|
| `ThinkingMs` | `8000` | Max think time per move (ms). |

#### Depth

| Property | Default | Description |
|---|---|---|
| `MinDepth` | `9` | Minimum depth before accepting a result. |
| `MaxDepth` | `60` | Hard cap on iterative deepening depth. |

#### Transposition table

| Property | Default | Description |
|---|---|---|
| `TranspositionTableSizePow2` | `21` | Entry count = 2^N. Each entry ≈ 32 bytes. |

Memory usage reference:

| Value | Entries | Memory |
|---|---|---|
| 19 | ~524 K | ~16 MB |
| 20 | ~1 M | ~32 MB |
| **21** | **~2 M** | **~64 MB** |
| 22 | ~4 M | ~128 MB |

#### Feature flags

| Property | Default | Description |
|---|---|---|
| `UseOpeningBook` | `true` | Use hardcoded Brazilian checkers openings. |
| `UseAspirationWindows` | `true` | Narrow α-β window around previous score. |
| `UseKillerMoves` | `true` | Store and prioritize quiet moves that caused cut-offs. |
| `UseHistoryHeuristic` | `true` | Reward historically strong quiet moves. |
| `UseLMR` | `true` | Reduce depth of late-ordered quiet moves. |
| `UseQuiescence` | `true` | Resolve captures at leaf nodes. |

#### LMR tuning

| Property | Default | Description |
|---|---|---|
| `LmrMinMoveIndex` | `4` | Apply LMR to moves at or beyond this index (0-based). |
| `LmrMinDepth` | `3` | Only apply LMR when remaining depth ≥ this value. |
| `LmrAggressiveIndex` | `8` | Reduce by 2 instead of 1 beyond this index. |

#### Aspiration windows

| Property | Default | Description |
|---|---|---|
| `AspirationWindowInitial` | `60` | Starting window half-width (centipawns). |
| `AspirationWindowMax` | `2000` | Maximum window before falling back to full-width. |

#### Draw advisor thresholds

| Property | Default | Description |
|---|---|---|
| `DrawRefuseAboveScore` | `200` | Refuse draw if material score ≥ this (2 pawns ahead). |
| `DrawAcceptBelowScore` | `-200` | Accept draw if material score ≤ this (2 pawns behind). |
| `DrawRepetitionThreshold` | `2` | Repetition count that triggers draw consideration. |

---

## Board Representation

### Coordinate system

```
    x=0  x=1  x=2  x=3  x=4  x=5  x=6  x=7
y=0  .    b    .    b    .    b    .    b     ← black start row
y=1  b    .    b    .    b    .    b    .
y=2  .    b    .    b    .    b    .    b
y=3  .    .    .    .    .    .    .    .     ← empty middle rows
y=4  .    .    .    .    .    .    .    .
y=5  r    .    r    .    r    .    r    .
y=6  .    r    .    r    .    r    .    r
y=7  r    .    r    .    r    .    r    .     ← red start row
```

- `b` = black pawn, `B` = black king (engine, promotes at y=7)
- `r` = red pawn,   `R` = red king   (human,  promotes at y=0)
- Only dark squares are used; light squares are always empty.

### Creating a Board

```csharp
// Standard starting position
var board = Board.StartingPosition();

// From a 2-D string array (row-major: grid[y][x])
var grid = new string?[8][];
// ... fill grid[y][x] with "b", "B", "r", "R", or null ...
var board = Board.FromArray(grid);

// From a JSON element array (for JSON-based protocols)
var board = Board.FromJson(jsonGrid);

// Serialize back to string array
string?[][] arr = board.ToArray();
```

### Applying moves

```csharp
Move mv = new Move(fx: 2, fy: 2, tx: 3, ty: 3);
Board next = board.Apply(mv);

// Or via the engine helper
Board next = BrazilianCheckersEngine.ApplyMove(board, mv);
```

---

## Draw Logic

### Accepting a human's draw offer

```csharp
var memory = new GameMemory();

// After the human proposes a draw:
DrawDecision decision = engine.ShouldAcceptDraw(board, memory);
if (decision.Accept)
    Console.WriteLine("Engine accepts: " + decision.Reason);
else
    Console.WriteLine("Engine refuses: " + decision.Reason);
```

### Offering a draw proactively

```csharp
// After applying the engine's move:
DrawOffer offer = engine.ShouldOfferDraw(board, memory);
if (offer.ShouldOffer)
    Console.WriteLine("Engine offers draw: " + offer.Message);
```

### Recording human moves (for repetition detection)

```csharp
// Before applying the human's move:
memory.RecordHumanMove(board, from: (x: 3, y: 5), to: (x: 4, y: 4));
board = board.Apply(humanMove);
```

### Draw reason codes

| Reason | Meaning |
|---|---|
| `"draw_known"` | Theoretical draw (e.g. king vs king) |
| `"draw_stuck"` | Position is locked with repeated moves |
| `"draw_losing"` | Engine is in a losing position with no counter-play |
| Portuguese string | Reason for refusing — suitable for display |

---

## Opponent Profiling

`GameMemory` tracks human move statistics across multiple games:

```csharp
var profile = memory.GetHumanProfile();

Console.WriteLine($"Aggression rate:  {profile.AggressionRate:P0}");  // % capture moves
Console.WriteLine($"Left flank rate:  {profile.LeftFlankRate:P0}");   // % moves from x≤3
Console.WriteLine($"Right flank rate: {profile.RightFlankRate:P0}");
Console.WriteLine($"Avg advance:      {profile.AvgAdvance:F2} rows");
Console.WriteLine($"Games learned:    {profile.GamesLearned} moves");
```

Call `memory.ResetGame()` between games (preserves cross-game stats).
Call `memory.ResetAll()` to wipe everything.

---

## Utility Methods

```csharp
// All legal moves for a side (maximum-capture rule enforced)
IReadOnlyList<Move> moves = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: true);

// Check for game over
bool gameOver = BrazilianCheckersEngine.IsGameOver(board, blackTurn: false);

// Static evaluation (for analysis — not a substitute for search)
int score = BrazilianCheckersEngine.Evaluate(board);
// score > 0 → black (engine) is ahead
// score < 0 → red (human) is ahead
```

---

## Building

```bash
# Debug
dotnet build checkers-engine/

# Release
dotnet build checkers-engine/ -c Release

# Self-contained binary (if used as a CLI)
dotnet publish checkers-engine/ -c Release -r win-x64 --self-contained
dotnet publish checkers-engine/ -c Release -r linux-x64 --self-contained
```

---

## Architecture

```
checkers-engine/
├── BrazilianCheckersEngine.cs   ← Public API — main entry point
├── EngineConfig.cs              ← Immutable configuration with presets
├── DrawAdvisor.cs               ← Heuristic draw accept/offer logic
├── GameMemory.cs                ← Position repetition + opponent profiling
├── CheckersEngine.csproj        ← Class library project file
└── Engine/
    ├── Piece.cs                 ← Piece enum + PieceHelper
    ├── Move.cs                  ← Move value type
    ├── Board.cs                 ← Board state, apply, factories
    ├── MoveGenerator.cs         ← Legal move generation (max-capture rule)
    ├── Evaluator.cs             ← Static position evaluation
    ├── TranspositionTable.cs    ← Zobrist TT, always-replace
    └── Search.cs                ← PVS + ID + aspiration + LMR + killers + history
```

### Score convention

All scores are from **black's perspective**:
- `+100` = black is one pawn ahead
- `+400` = black is one king ahead
- `+100 000` = black wins (forced mate)
- `-100 000` = black loses (forced mate)

### Rule implementation notes

- **Mandatory capture** — `MoveGenerator` returns only captures when any exist.
- **Maximum capture rule** — only the capture sequence(s) with the greatest chain length are returned.
- **Dama-voadora** — a pawn reaching the back rank mid-chain is not promoted until the chain ends.
- **King re-capture prevention** — sentinel markers on captured squares prevent kings from re-crossing them within the same chain.

---

## Differences from `services/checkers_bot`

| Aspect | `services/checkers_bot` | `checkers-engine` |
|---|---|---|
| Output type | Console app (stdin/stdout JSON) | Class library (direct method calls) |
| API | JSON newline-delimited protocol | Typed C# methods |
| Configuration | Hardcoded constants | `EngineConfig` record with presets |
| Draw logic | Implemented in Node.js (`CheckersBotService.js`) | Ported to C# (`DrawAdvisor.cs`) |
| Board factory | `FromJson` only | `FromJson`, `FromArray`, `StartingPosition`, `ToArray` |
| TT size | Fixed 2^21 | Configurable 2^[16-24] |
| Think time | Fixed 8 s | Configurable per-instance or per-call |
| Opening book | Always enabled | Togglable via `UseOpeningBook` |
| All search features | Always enabled | Individually togglable for testing |

---

## License

Internal project — all rights reserved.
