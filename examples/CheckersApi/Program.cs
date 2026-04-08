// CheckersApi — minimal ASP.NET Core HTTP wrapper around CheckersEngine.
// Run:  dotnet run --project examples/CheckersApi
// Port: 5000 (HTTP) | set CHECKERS_PORT env var to change
// Tablebase: set CHECKERS_TABLEBASE_PATH env to auto-load on startup.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheckersEngine;
using CheckersEngine.Engine;
using CheckersEngine.Engine.Tablebase;

// ─── Config ──────────────────────────────────────────────────────────────────

int port        = int.TryParse(Environment.GetEnvironmentVariable("CHECKERS_PORT"), out var p) ? p : 5000;
string? tbPath  = Environment.GetEnvironmentVariable("CHECKERS_TABLEBASE_PATH");

// ─── Startup ──────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Consistent JSON: camelCase, ignore nulls
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// ─── Shared state ─────────────────────────────────────────────────────────────

EndgameTablebase? tablebase = null;
if (tbPath != null && Directory.Exists(tbPath))
{
    Console.WriteLine($"[startup] Loading tablebase from {tbPath} …");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    tablebase = EndgameTablebase.Load(tbPath);
    Console.WriteLine($"[startup] Tablebase ready — {tablebase.ConfigCount} configs, " +
                      $"{tablebase.TotalPositions:N0} positions ({sw.ElapsedMilliseconds} ms)");
}

var games = new ConcurrentDictionary<string, GameState>();

// ─── Helpers ──────────────────────────────────────────────────────────────────

static EngineConfig ResolveConfig(StartRequest req)
{
    var cfg = req.Preset?.ToLowerInvariant() switch
    {
        "blitz"  => EngineConfig.Blitz,
        "fast"   => EngineConfig.Fast,
        "strong" => EngineConfig.Strong,
        _        => EngineConfig.Default,
    };
    if (req.ThinkingMs    is { } ms)  cfg = cfg with { ThinkingMs    = ms };
    if (req.MinDepth      is { } md)  cfg = cfg with { MinDepth      = md };
    if (req.UseOpeningBook is { } ob) cfg = cfg with { UseOpeningBook = ob };
    if (req.UseLMR        is { } lmr) cfg = cfg with { UseLMR        = lmr };
    if (req.UseQuiescence is { } q)   cfg = cfg with { UseQuiescence  = q };
    return cfg;
}

static object MoveDto(Move mv) => new
{
    fx = mv.Fx, fy = mv.Fy,
    tx = mv.Tx, ty = mv.Ty,
    isCapture = mv.IsCapture,
};

static object GameDto(GameState gs) => new
{
    board    = gs.Board.ToArray(),
    turn     = gs.BlackTurn ? "black" : "red",
    gameOver = gs.GameOver,
    winner   = gs.Winner,
};

// ─── Routes ───────────────────────────────────────────────────────────────────

// GET /health
app.MapGet("/health", () => Results.Ok(new
{
    status     = "ok",
    tablebase  = tablebase != null,
    tbConfigs  = tablebase?.ConfigCount ?? 0,
    tbPositions = tablebase?.TotalPositions ?? 0,
}));

// POST /tablebase/generate  — generate (or regenerate) the tablebase at runtime
// Body: { "maxPieces": 6, "outputPath": "./tablebase" }
app.MapPost("/tablebase/generate", (TablebaseGenRequest req) =>
{
    int    max     = req.MaxPieces  ?? 6;
    string outPath = req.OutputPath ?? "./tablebase";

    if (max < 2 || max > 8)
        return Results.BadRequest(new { error = "maxPieces must be 2–8" });

    var progress = new List<object>();
    var cb = new Progress<GenerationProgress>(p => progress.Add(new
    {
        config      = p.Config,
        positions   = p.Positions,
        wins        = p.Wins,
        losses      = p.Losses,
        draws       = p.Draws,
        elapsedMs   = p.ElapsedMs,
    }));

    var tb = EndgameTablebase.Generate(max, cb);
    tb.Save(outPath);

    tablebase = tb; // hot-swap into shared state

    return Results.Ok(new
    {
        maxPieces   = max,
        outputPath  = outPath,
        configs     = tb.ConfigCount,
        totalPositions = tb.TotalPositions,
        diskBytes   = tb.DiskBytes,
        configLog   = progress,
    });
});

// POST /game/start
// Body: { "preset": "default", "thinkingMs": 8000, "minDepth": 9,
//         "useOpeningBook": true, "useLMR": true, "useQuiescence": true }
app.MapPost("/game/start", (StartRequest req) =>
{
    var cfg = ResolveConfig(req);

    // Use a tablebase loaded at path from this request, or fall back to the global one
    EndgameTablebase? tb = tablebase;
    if (req.TablebasePath is { } tbp && Directory.Exists(tbp) && tbp != tbPath)
        tb = EndgameTablebase.Load(tbp);

    string id = Guid.NewGuid().ToString("N")[..12];
    var gs    = new GameState(new BrazilianCheckersEngine(cfg, tb), new GameMemory());
    games[id] = gs;

    return Results.Ok(new
    {
        gameId     = id,
        board      = gs.Board.ToArray(),
        turn       = "black",
        tablebase  = tb != null,
    });
});

// GET /game/{id}
app.MapGet("/game/{id}", (string id) =>
    games.TryGetValue(id, out var gs)
        ? Results.Ok(GameDto(gs))
        : Results.NotFound(new { error = "Game not found" }));

// POST /game/{id}/engine-move  — engine (black) makes its move
app.MapPost("/game/{id}/engine-move", (string id) =>
{
    if (!games.TryGetValue(id, out var gs))
        return Results.NotFound(new { error = "Game not found" });
    if (gs.GameOver)
        return Results.BadRequest(new { error = "Game is already over" });
    if (!gs.BlackTurn)
        return Results.BadRequest(new { error = "It is not the engine's turn (black)" });

    Move? mv = gs.Engine.FindBestMove(gs.Board);
    if (mv == null)
    {
        gs.GameOver = true;
        gs.Winner   = "red";
        return Results.Ok(new { gameOver = true, winner = "red" });
    }

    gs.Board     = gs.Board.Apply(mv.Value);
    gs.BlackTurn = false;

    // Check if red now has no moves
    bool redGameOver = BrazilianCheckersEngine.IsGameOver(gs.Board, blackTurn: false);
    if (redGameOver) { gs.GameOver = true; gs.Winner = "black"; }

    // Proactive draw offer from engine
    DrawOffer offer = gs.Engine.ShouldOfferDraw(gs.Board, gs.Memory);

    // Optional tablebase probe of resulting position
    TablebaseResult? tbResult = gs.Engine.ProbeTablebase(gs.Board, blackTurn: false);

    return Results.Ok(new
    {
        move      = MoveDto(mv.Value),
        board     = gs.Board.ToArray(),
        turn      = "red",
        gameOver  = gs.GameOver,
        winner    = gs.Winner,
        drawOffer = offer.ShouldOffer ? offer.Message : null,
        tablebase = tbResult.HasValue ? new
        {
            outcome = tbResult.Value.Outcome.ToString().ToLower(),
            dtm     = tbResult.Value.Dtm,
            label   = tbResult.Value.ToString(),
        } : null,
    });
});

// POST /game/{id}/human-move
// Body: { "fx": 1, "fy": 5, "tx": 2, "ty": 4 }
app.MapPost("/game/{id}/human-move", (string id, HumanMoveRequest req) =>
{
    if (!games.TryGetValue(id, out var gs))
        return Results.NotFound(new { error = "Game not found" });
    if (gs.GameOver)
        return Results.BadRequest(new { error = "Game is already over" });
    if (gs.BlackTurn)
        return Results.BadRequest(new { error = "It is not the human's turn (red)" });

    // Validate that the move is legal
    var legalMoves = BrazilianCheckersEngine.GetLegalMoves(gs.Board, blackTurn: false);
    bool isCapture = Math.Abs(req.Tx - req.Fx) > 1;
    var  requested = new Move(req.Fx, req.Fy, req.Tx, req.Ty, isCapture);

    if (!legalMoves.Any(m => m.Equals(requested)))
        return Results.BadRequest(new { error = "Illegal move", legal = legalMoves.Select(MoveDto) });

    // Record for repetition / profiling
    gs.Memory.RecordHumanMove(gs.Board, (req.Fx, req.Fy), (req.Tx, req.Ty));
    gs.Board     = gs.Board.Apply(requested);
    gs.BlackTurn = true;

    // Check if black now has no moves
    bool blackGameOver = BrazilianCheckersEngine.IsGameOver(gs.Board, blackTurn: true);
    if (blackGameOver) { gs.GameOver = true; gs.Winner = "red"; }

    return Results.Ok(new
    {
        board    = gs.Board.ToArray(),
        turn     = "black",
        gameOver = gs.GameOver,
        winner   = gs.Winner,
    });
});

// GET /game/{id}/legal-moves?black=true
app.MapGet("/game/{id}/legal-moves", (string id, bool? black) =>
{
    if (!games.TryGetValue(id, out var gs))
        return Results.NotFound(new { error = "Game not found" });

    bool side    = black ?? gs.BlackTurn;
    var  moves   = BrazilianCheckersEngine.GetLegalMoves(gs.Board, side);
    return Results.Ok(new { side = side ? "black" : "red", moves = moves.Select(MoveDto) });
});

// POST /game/{id}/draw/offer   — human offers draw; engine decides
app.MapPost("/game/{id}/draw/offer", (string id) =>
{
    if (!games.TryGetValue(id, out var gs))
        return Results.NotFound(new { error = "Game not found" });

    DrawDecision d = gs.Engine.ShouldAcceptDraw(gs.Board, gs.Memory);
    if (d.Accept) { gs.GameOver = true; gs.Winner = null; }

    return Results.Ok(new { accepted = d.Accept, reason = d.Reason, gameOver = gs.GameOver });
});

// GET /game/{id}/draw/should-offer  — check if engine wants to proactively offer
app.MapGet("/game/{id}/draw/should-offer", (string id) =>
{
    if (!games.TryGetValue(id, out var gs))
        return Results.NotFound(new { error = "Game not found" });

    DrawOffer offer = gs.Engine.ShouldOfferDraw(gs.Board, gs.Memory);
    return Results.Ok(new { shouldOffer = offer.ShouldOffer, message = offer.Message });
});

// GET /game/{id}/probe  — probe tablebase for current position
app.MapGet("/game/{id}/probe", (string id) =>
{
    if (!games.TryGetValue(id, out var gs))
        return Results.NotFound(new { error = "Game not found" });

    var r = gs.Engine.ProbeTablebase(gs.Board, gs.BlackTurn);
    return Results.Ok(new
    {
        covered = r.HasValue,
        outcome = r?.Outcome.ToString().ToLower(),
        dtm     = r?.Dtm,
        label   = r?.ToString(),
    });
});

// GET /game/{id}/profile  — human player behavioral profile
app.MapGet("/game/{id}/profile", (string id) =>
{
    if (!games.TryGetValue(id, out var gs))
        return Results.NotFound(new { error = "Game not found" });

    var prof = gs.Memory.GetHumanProfile();
    return Results.Ok(new
    {
        aggressionRate  = prof.AggressionRate,
        leftFlankRate   = prof.LeftFlankRate,
        rightFlankRate  = prof.RightFlankRate,
        avgAdvance      = prof.AvgAdvance,
        gamesLearned    = prof.GamesLearned,
    });
});

// POST /game/{id}/reset  — reset board but keep cross-game memory
app.MapPost("/game/{id}/reset", (string id) =>
{
    if (!games.TryGetValue(id, out var gs))
        return Results.NotFound(new { error = "Game not found" });

    gs.Memory.ResetGame();
    gs.Board     = Board.StartingPosition();
    gs.BlackTurn = true;
    gs.GameOver  = false;
    gs.Winner    = null;

    return Results.Ok(new { board = gs.Board.ToArray(), turn = "black" });
});

// DELETE /game/{id}
app.MapDelete("/game/{id}", (string id) =>
{
    games.TryRemove(id, out _);
    return Results.Ok(new { deleted = id });
});

// ─── Run ──────────────────────────────────────────────────────────────────────

Console.WriteLine($"CheckersApi listening on http://0.0.0.0:{port}");
Console.WriteLine($"Tablebase: {(tablebase != null ? $"loaded ({tablebase.ConfigCount} configs)" : "not loaded")}");
Console.WriteLine("Endpoints: GET /health | POST /game/start | POST /game/{{id}}/engine-move | ...");

app.Run();

// ─── Domain types ─────────────────────────────────────────────────────────────

sealed class GameState(BrazilianCheckersEngine engine, GameMemory memory)
{
    public BrazilianCheckersEngine Engine = engine;
    public GameMemory              Memory = memory;
    public Board                   Board  = Board.StartingPosition();
    public bool                    BlackTurn = true;
    public bool                    GameOver  = false;
    public string?                 Winner    = null;
}

// ─── Request / response models ────────────────────────────────────────────────

record StartRequest(
    string?  Preset        = null,
    int?     ThinkingMs    = null,
    int?     MinDepth      = null,
    bool?    UseOpeningBook = null,
    bool?    UseLMR        = null,
    bool?    UseQuiescence = null,
    string?  TablebasePath = null);

record HumanMoveRequest(int Fx, int Fy, int Tx, int Ty);

record TablebaseGenRequest(int? MaxPieces = null, string? OutputPath = null);
