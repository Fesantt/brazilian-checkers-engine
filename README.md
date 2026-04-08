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

Reference the library directly from your `.csproj`:

```xml
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

// 1. Create the engine (default: 8 s per move, all features enabled)
var engine = new BrazilianCheckersEngine();

// 2. Get the standard starting position
var board = Board.StartingPosition();

// 3. Ask for the best move (engine plays black)
Move? best = engine.FindBestMove(board);
if (best.HasValue)
{
    Console.WriteLine($"Engine plays: {best.Value}");  // e.g. "(1,2)->(2,3)"
    board = board.Apply(best.Value);
}

// 4. Check if the game ended
if (BrazilianCheckersEngine.IsGameOver(board, blackTurn: false))
    Console.WriteLine("Red (human) has no moves — engine wins!");
```

---

## Configuration

All settings live in `EngineConfig`, an immutable record with `init`-only properties.

### Presets

```csharp
EngineConfig.Blitz   // 1 s,  depth ≥ 3,  small TT (~16 MB)
EngineConfig.Fast    // 2 s,  depth ≥ 5,  medium TT (~32 MB)
EngineConfig.Default // 8 s,  depth ≥ 9,  standard TT (~64 MB)
EngineConfig.Strong  // 15 s, depth ≥ 12, large TT (~128 MB)
```

### Custom configuration

Use C# `with` expressions to override individual properties:

```csharp
var cfg = EngineConfig.Default with
{
    ThinkingMs     = 5_000,      // 5 seconds per move
    MinDepth       = 7,          // accept result after depth 7
    UseOpeningBook = false,      // disable opening book (useful for testing)
    UseQuiescence  = false,      // disable quiescence (faster, weaker)
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
var grid = new string?[8][]
{
    new string?[] { null, "b", null, "b", null, "b", null, "b" },
    new string?[] { "b",  null, "b", null, "b", null, "b", null },
    new string?[] { null, "b", null, "b", null, "b", null, "b" },
    new string?[] { null, null, null, null, null, null, null, null },
    new string?[] { null, null, null, null, null, null, null, null },
    new string?[] { "r",  null, "r", null, "r", null, "r", null },
    new string?[] { null, "r", null, "r", null, "r", null, "r" },
    new string?[] { "r",  null, "r", null, "r", null, "r", null },
};
var board = Board.FromArray(grid);

// From a JSON element array (for JSON-based protocols)
var board = Board.FromJson(jsonGrid);

// Serialize back to string array (null = empty square)
string?[][] arr = board.ToArray();
```

### Applying moves

```csharp
// Simple move: piece at (1,2) moves to (2,3)
Move mv = new Move(fx: 1, fy: 2, tx: 2, ty: 3);
Board next = board.Apply(mv);

// Capture move: piece at (1,2) jumps over (2,3) to land at (3,4)
Move capture = new Move(fx: 1, fy: 2, tx: 3, ty: 4, isCapture: true);
Board next = board.Apply(capture);

// Via the static helper
Board next = BrazilianCheckersEngine.ApplyMove(board, mv);
```

---

## Complete Game Loop Example

This shows a full game cycle including draw logic and memory tracking:

```csharp
using CheckersEngine;
using CheckersEngine.Engine;

var engine = new BrazilianCheckersEngine(EngineConfig.Default);
var memory = new GameMemory();
var board  = Board.StartingPosition();
bool blackTurn = true; // engine moves first

while (true)
{
    // Check if the current side has no moves (game over)
    if (BrazilianCheckersEngine.IsGameOver(board, blackTurn))
    {
        Console.WriteLine(blackTurn ? "Black has no moves — red wins!" : "Red has no moves — black wins!");
        break;
    }

    if (blackTurn)
    {
        // Engine's turn
        Move? best = engine.FindBestMove(board);
        if (!best.HasValue) break;

        Console.WriteLine($"Engine: {best.Value}");
        board = board.Apply(best.Value);

        // After engine moves, check if it wants to offer a draw
        DrawOffer offer = engine.ShouldOfferDraw(board, memory);
        if (offer.ShouldOffer)
            Console.WriteLine($"Engine offers draw: {offer.Message}");
    }
    else
    {
        // Human's turn — read move from input
        Console.Write("Your move (fx fy tx ty): ");
        var parts = Console.ReadLine()!.Split(' ');
        int fx = int.Parse(parts[0]), fy = int.Parse(parts[1]);
        int tx = int.Parse(parts[2]), ty = int.Parse(parts[3]);
        bool isCapture = Math.Abs(tx - fx) > 1;

        // Record human move BEFORE applying it (for repetition tracking)
        memory.RecordHumanMove(board, (fx, fy), (tx, ty));
        board = board.Apply(new Move(fx, fy, tx, ty, isCapture));

        // If human offers draw, consult the advisor
        Console.Write("Offer draw? (y/n): ");
        if (Console.ReadLine() == "y")
        {
            DrawDecision d = engine.ShouldAcceptDraw(board, memory);
            Console.WriteLine(d.Accept ? $"Draw accepted: {d.Reason}" : $"Draw refused: {d.Reason}");
            if (d.Accept) break;
        }
    }

    blackTurn = !blackTurn;
}

memory.ResetGame(); // call at end of each game; preserves cross-game stats
```

### Chain captures

When a capture chain is in progress, pass the active piece's coordinates so the engine only considers continuations from that piece:

```csharp
// The piece at (3,4) just captured and must continue
Move? next = engine.FindBestMove(board, activePieceX: 3, activePieceY: 4);
```

---

## Node.js Integration

This library is a **.NET class library**, so Node.js cannot call it directly. The recommended approach is to wrap it in a minimal **ASP.NET Core HTTP API** and call that from Node.

### 1. Create the ASP.NET Core wrapper (C#)

Create a new project alongside the library:

```bash
dotnet new web -n CheckersApi
cd CheckersApi
dotnet add reference ../checkers-engine/CheckersEngine.csproj
```

`Program.cs`:

```csharp
using CheckersEngine;
using CheckersEngine.Engine;

var builder = WebApplication.CreateBuilder(args);
var app     = builder.Build();

// One engine instance per game — store in a dictionary keyed by session ID
var engines  = new Dictionary<string, BrazilianCheckersEngine>();
var memories = new Dictionary<string, GameMemory>();

// ─── POST /game/start ───────────────────────────────────────────────────────
// Body: { "preset": "default" | "fast" | "strong" | "blitz" }
//       or a full config object with individual properties
app.MapPost("/game/start", (StartRequest req) =>
{
    string id = Guid.NewGuid().ToString("N");

    EngineConfig cfg = req.Preset?.ToLower() switch
    {
        "blitz"  => EngineConfig.Blitz,
        "fast"   => EngineConfig.Fast,
        "strong" => EngineConfig.Strong,
        _        => EngineConfig.Default,
    };

    // Fine-tune individual properties when provided
    if (req.ThinkingMs.HasValue)       cfg = cfg with { ThinkingMs       = req.ThinkingMs.Value };
    if (req.MinDepth.HasValue)         cfg = cfg with { MinDepth         = req.MinDepth.Value };
    if (req.UseOpeningBook.HasValue)   cfg = cfg with { UseOpeningBook   = req.UseOpeningBook.Value };
    if (req.UseLMR.HasValue)           cfg = cfg with { UseLMR           = req.UseLMR.Value };

    engines[id]  = new BrazilianCheckersEngine(cfg);
    memories[id] = new GameMemory();

    return Results.Ok(new { gameId = id, board = Board.StartingPosition().ToArray() });
});

// ─── POST /game/{id}/move ───────────────────────────────────────────────────
// Body: { "board": [[...]], "thinkingMs": 5000 }
app.MapPost("/game/{id}/move", (string id, MoveRequest req) =>
{
    if (!engines.TryGetValue(id, out var engine))
        return Results.NotFound(new { error = "Game not found" });

    var board = Board.FromArray(req.Board);
    Move? best = engine.FindBestMove(board, thinkingMs: req.ThinkingMs ?? 0);

    if (!best.HasValue)
        return Results.Ok(new { gameOver = true, winner = "red" });

    var next = board.Apply(best.Value);
    var offer = engine.ShouldOfferDraw(next, memories[id]);

    return Results.Ok(new
    {
        move     = new { fx = best.Value.Fx, fy = best.Value.Fy,
                         tx = best.Value.Tx, ty = best.Value.Ty,
                         isCapture = best.Value.IsCapture },
        board    = next.ToArray(),
        drawOffer = offer.ShouldOffer ? offer.Message : null,
    });
});

// ─── POST /game/{id}/draw ───────────────────────────────────────────────────
// Body: { "board": [[...]] }
app.MapPost("/game/{id}/draw", (string id, BoardRequest req) =>
{
    if (!engines.TryGetValue(id, out var engine))
        return Results.NotFound(new { error = "Game not found" });

    var board    = Board.FromArray(req.Board);
    var decision = engine.ShouldAcceptDraw(board, memories[id]);

    return Results.Ok(new { accept = decision.Accept, reason = decision.Reason });
});

// ─── DELETE /game/{id} ──────────────────────────────────────────────────────
app.MapDelete("/game/{id}", (string id) =>
{
    engines.Remove(id);
    memories.Remove(id);
    return Results.Ok();
});

app.Run("http://localhost:5000");

// ─── Request models ──────────────────────────────────────────────────────────
record StartRequest(
    string?  Preset        = null,
    int?     ThinkingMs    = null,
    int?     MinDepth      = null,
    bool?    UseOpeningBook = null,
    bool?    UseLMR        = null);

record MoveRequest(string?[][] Board, int? ThinkingMs = null);
record BoardRequest(string?[][] Board);
```

Run it:

```bash
dotnet run --project CheckersApi
```

---

### 2. Call from Node.js

Install `node-fetch` (or use the native `fetch` available in Node 18+):

```bash
npm install node-fetch   # only needed for Node < 18
```

#### `checkers-client.js`

```js
const BASE = 'http://localhost:5000';

// ─── Start a game ─────────────────────────────────────────────────────────

async function startGame(options = {}) {
  const res = await fetch(`${BASE}/game/start`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      preset: options.preset ?? 'default',   // 'blitz' | 'fast' | 'default' | 'strong'
      thinkingMs:    options.thinkingMs,     // override think time (ms)
      minDepth:      options.minDepth,       // override min depth
      useOpeningBook: options.useOpeningBook, // true | false
      useLMR:        options.useLMR,         // true | false
    }),
  });
  return res.json(); // { gameId, board }
}

// ─── Ask the engine to move ───────────────────────────────────────────────

async function engineMove(gameId, board, thinkingMs) {
  const res = await fetch(`${BASE}/game/${gameId}/move`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ board, thinkingMs }),
  });
  return res.json();
  // { move: { fx, fy, tx, ty, isCapture }, board, drawOffer }
}

// ─── Ask if the engine accepts a draw ────────────────────────────────────

async function askDraw(gameId, board) {
  const res = await fetch(`${BASE}/game/${gameId}/draw`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ board }),
  });
  return res.json(); // { accept: bool, reason: string }
}

// ─── End the game session ─────────────────────────────────────────────────

async function endGame(gameId) {
  await fetch(`${BASE}/game/${gameId}`, { method: 'DELETE' });
}

// ─── Example usage ────────────────────────────────────────────────────────

(async () => {
  // Start a game with a custom config
  const { gameId, board: startBoard } = await startGame({
    preset: 'fast',       // base preset
    thinkingMs: 3000,     // override to 3 s
    useOpeningBook: true,
  });

  console.log('Game started:', gameId);

  let board = startBoard;

  // Let the engine make its first move
  const result = await engineMove(gameId, board);
  console.log('Engine move:', result.move);  // e.g. { fx: 1, fy: 2, tx: 2, ty: 3 }
  board = result.board;

  if (result.drawOffer) {
    console.log('Engine offers draw:', result.drawOffer);
  }

  // Later — human offers draw
  const drawCheck = await askDraw(gameId, board);
  console.log('Draw accepted?', drawCheck.accept, '—', drawCheck.reason);

  // Clean up
  await endGame(gameId);
})();
```

#### Passing config options from Node

| Option | Type | Example | Description |
|---|---|---|---|
| `preset` | `string` | `"fast"` | Base preset (`blitz`, `fast`, `default`, `strong`) |
| `thinkingMs` | `number` | `3000` | Override think time in milliseconds |
| `minDepth` | `number` | `6` | Override minimum search depth |
| `useOpeningBook` | `boolean` | `false` | Toggle the opening book |
| `useLMR` | `boolean` | `true` | Toggle Late Move Reductions |

All options are optional — omit any to use the preset's value.

---

## Endgame Tablebase

The library ships with a built-in retrograde-analysis tablebase generator.
All positions with at most N total pieces are solved with **perfect play** from both sides.

### How it works

The generator uses **retrograde analysis** (backward induction):

1. **Phase 1 — Terminal & cross-config:** scan every position. If a side has no legal moves → Loss. For each capture move, look up the result in the already-generated sub-tablebase (one fewer piece).
2. **Phase 2 — BFS retrograde:** propagate Win/Loss backwards through non-capture moves within the same configuration using reverse move generation.
3. **Phase 3 — Draw:** any position still unresolved after convergence is a Draw.

Configurations are generated from fewest pieces to most, so cross-config lookups are always ready.

### Generating and saving

```csharp
using CheckersEngine.Engine.Tablebase;

// Show progress per configuration
var progress = new Progress<GenerationProgress>(p =>
    Console.WriteLine($"  {p.Config,-30} {p.Positions,12:N0} positions  {p.Wins,10:N0} W  {p.Losses,10:N0} L  {p.Draws,10:N0} D  ({p.ElapsedMs} ms)")
);

// Generate all configs with ≤ 6 pieces (one-time, then save to disk)
var tb = EndgameTablebase.Generate(maxPieces: 6, progress: progress);
tb.Save("./tablebase");

Console.WriteLine($"Configs: {tb.ConfigCount},  Positions: {tb.TotalPositions:N0},  Disk: {tb.DiskBytes / 1_048_576.0:F1} MB");
```

**Memory during generation (approximate peak per configuration):**

| Max pieces | Peak memory (worst config) | Disk size (total) |
|---|---|---|
| 4 | < 10 MB | < 2 MB |
| 5 | ~150 MB | ~20 MB |
| 6 | up to ~2 GB | ~500 MB |

### Generating a single specific configuration

```csharp
// Generate only the "1 black king vs 2 red kings" endgame
// (all sub-configs needed for capture lookups are auto-generated)
var tb = EndgameTablebase.GenerateConfig(bp: 0, bk: 1, rp: 0, rk: 2, progress: progress);
tb.Save("./tablebase");
```

### Loading

```csharp
// Load all .tb files from the directory (fast — a few seconds)
var tb = EndgameTablebase.Load("./tablebase");

// Inspect loaded configs
foreach (string line in tb.ConfigSummary())
    Console.WriteLine(line);
// Output:
//   0bp+1bk vs 0rp+1rk  (32 positions/side)
//   0bp+1bk vs 1rp+0rk  (1,024 positions/side)
//   ...
```

### Probing positions

```csharp
// Probe a position directly
TablebaseResult? result = tb.Probe(board, blackTurn: true);

if (result.HasValue)
{
    Console.WriteLine(result.Value);          // "Win in 4 moves" / "Loss in 7 moves" / "Draw"
    Console.WriteLine(result.Value.Outcome);  // TablebaseOutcome.Win / Loss / Draw
    Console.WriteLine(result.Value.Dtm);      // distance-to-mate in plies
}

// Get the optimal move from the tablebase
Move? best = tb.BestMove(board, blackTurn: true);
// Prefers: Win (fastest) > Draw > Loss (slowest)
```

### Integrating with the engine

Pass the tablebase when constructing the engine. Positions covered by the tablebase are resolved **instantly** without running the full search:

```csharp
// Generate (or load) the tablebase
var tb = EndgameTablebase.Load("./tablebase");

// Engine now uses TB for endgames automatically
var engine = new BrazilianCheckersEngine(EngineConfig.Default, tablebase: tb);

// Probe manually if needed (e.g. to show the result to the user)
TablebaseResult? r = engine.ProbeTablebase(board, blackTurn: true);
```

### Node.js integration with tablebase

Add the tablebase endpoint to the ASP.NET Core wrapper:

```csharp
// POST /game/start
// Body: { "preset": "default", "tablebasePath": "./tablebase" }
app.MapPost("/game/start", (StartRequest req) =>
{
    string id = Guid.NewGuid().ToString("N");

    EndgameTablebase? tb = null;
    if (!string.IsNullOrEmpty(req.TablebasePath) && Directory.Exists(req.TablebasePath))
        tb = EndgameTablebase.Load(req.TablebasePath);

    var cfg = /* resolve preset */ EngineConfig.Default;
    engines[id]  = new BrazilianCheckersEngine(cfg, tb);
    memories[id] = new GameMemory();

    return Results.Ok(new { gameId = id, tablebaseLoaded = tb != null });
});

// GET /game/{id}/probe — probe the tablebase for a position
app.MapPost("/game/{id}/probe", (string id, BoardRequest req) =>
{
    if (!engines.TryGetValue(id, out var engine)) return Results.NotFound();
    var board  = Board.FromArray(req.Board);
    var result = engine.ProbeTablebase(board, blackTurn: req.BlackTurn ?? true);
    return Results.Ok(new
    {
        covered = result.HasValue,
        outcome = result?.Outcome.ToString(),  // "Win" / "Loss" / "Draw" / null
        dtm     = result?.Dtm,
    });
});
```

From Node.js:

```js
// Start a game with tablebase
const { gameId } = await fetch(`${BASE}/game/start`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ preset: 'default', tablebasePath: './tablebase' }),
}).then(r => r.json());

// Probe a position (e.g. to display TB result to the user)
const probe = await fetch(`${BASE}/game/${gameId}/probe`, {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ board: currentBoard, blackTurn: true }),
}).then(r => r.json());

if (probe.covered) {
  console.log(`TB says: ${probe.outcome} in ${Math.ceil(probe.dtm / 2)} moves`);
}
```

### Generating the tablebase from a standalone script

```csharp
// GenerateTablebase.cs (add as a dotnet-script or Console app)
using CheckersEngine.Engine.Tablebase;

int maxPieces = args.Length > 0 ? int.Parse(args[0]) : 6;
string outDir = args.Length > 1 ? args[1] : "./tablebase";

Console.WriteLine($"Generating tablebase (maxPieces={maxPieces}) → {outDir}");

var progress = new Progress<GenerationProgress>(p =>
    Console.WriteLine($"  [{p.ElapsedMs,6} ms] {p.Config,-30}  {p.Positions,12:N0} positions  "
                    + $"W:{p.Wins:N0}  L:{p.Losses:N0}  D:{p.Draws:N0}")
);

var sw = System.Diagnostics.Stopwatch.StartNew();
var tb = EndgameTablebase.Generate(maxPieces, progress);
tb.Save(outDir);

Console.WriteLine($"\nDone in {sw.Elapsed.TotalSeconds:F1} s");
Console.WriteLine($"  Configs:   {tb.ConfigCount}");
Console.WriteLine($"  Positions: {tb.TotalPositions:N0}");
Console.WriteLine($"  Disk:      {tb.DiskBytes / 1_048_576.0:F1} MB");
```

Run it:

```bash
dotnet run --project GenerateTablebase -- 6 ./tablebase
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
// BEFORE applying the human's move:
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

`GameMemory` accumulates human move statistics across multiple games and exposes a statistical profile:

```csharp
var profile = memory.GetHumanProfile();

Console.WriteLine($"Aggression rate:  {profile.AggressionRate:P0}");  // % capture moves
Console.WriteLine($"Left flank rate:  {profile.LeftFlankRate:P0}");   // % moves from x ≤ 3
Console.WriteLine($"Right flank rate: {profile.RightFlankRate:P0}");  // % moves from x ≥ 4
Console.WriteLine($"Avg advance:      {profile.AvgAdvance:F2} rows"); // average row advancement
Console.WriteLine($"Moves learned:    {profile.GamesLearned}");       // total moves recorded
```

- Data comes from all games once ≥ 10 moves have been recorded; otherwise uses current game only.
- Call `memory.ResetGame()` between games — preserves cross-game stats.
- Call `memory.ResetAll()` to wipe everything.

---

## Utility Methods

```csharp
// All legal moves for a side (maximum-capture rule enforced)
IReadOnlyList<Move> moves = BrazilianCheckersEngine.GetLegalMoves(board, blackTurn: true);

foreach (var mv in moves)
    Console.WriteLine(mv); // "(fx,fy)->(tx,ty)" or "(fx,fy)->(tx,ty)x" for captures

// Check for game over (current side has no legal moves)
bool gameOver = BrazilianCheckersEngine.IsGameOver(board, blackTurn: false);

// Static evaluation (for analysis — not a substitute for full search)
int score = BrazilianCheckersEngine.Evaluate(board);
// score > 0 → black (engine) is ahead
// score < 0 → red (human) is ahead
// |score| ≥ 100_000 → forced win/loss

// Count pieces
var (blackPawns, blackKings, redPawns, redKings) = board.CountPieces();
```

---

## Evaluation Score Reference

| Score | Meaning |
|---|---|
| `+100` | Black is one pawn ahead |
| `+400` | Black is one king ahead |
| `+100 000` | Black wins (no legal moves for red) |
| `-100 000` | Black loses (no legal moves for black) |
| `0` | Roughly equal position |

Positional components (additive, all from black's perspective):
- **Material** — pawn = 100 cp, king = 400 cp
- **Piece-square tables** — pawn advancement bonus + king centrality
- **Mobility** — number of legal moves available (weight increases in endgame)
- **Threats** — penalty for pieces immediately capturable
- **Backed pieces** — bonus for pieces with a diagonal friendly neighbor
- **Pre-promotion** — bonus for pawns one row from the back rank
- **Border penalty** — edge-file pawns penalized in middlegame
- **King pursuit** — in deep endgames, bonus for kings close to enemy pieces

---

## Building

```bash
# Debug
dotnet build checkers-engine/

# Release
dotnet build checkers-engine/ -c Release

# Self-contained binary (if used as a CLI wrapper)
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
    ├── Move.cs                  ← Move value type (fx, fy, tx, ty, isCapture)
    ├── Board.cs                 ← Board state, Apply, factories, CountPieces
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

### Thread safety

A single `BrazilianCheckersEngine` instance is **not** thread-safe. Create one instance per concurrent game.

---

## Differences from `services/checkers_bot`

| Aspect | `services/checkers_bot` | `checkers-engine` |
|---|---|---|
| Output type | Console app (stdin/stdout JSON) | Class library (direct method calls) |
| API | JSON newline-delimited protocol | Typed C# methods |
| Configuration | Hardcoded constants | `EngineConfig` record with presets |
| Draw logic | Implemented in Node.js (`CheckersBotService.js`) | Ported to C# (`DrawAdvisor.cs`) |
| Board factory | `FromJson` only | `FromJson`, `FromArray`, `StartingPosition`, `ToArray` |
| TT size | Fixed 2^21 | Configurable 2^[16–24] |
| Think time | Fixed 8 s | Configurable per-instance or per-call |
| Opening book | Always enabled | Togglable via `UseOpeningBook` |
| All search features | Always enabled | Individually togglable for testing |

---

## License

Internal project — all rights reserved.
