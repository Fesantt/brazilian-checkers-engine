using CheckersEngine.Engine.Tablebase;
using System.Diagnostics;

// ─── Argument parsing ─────────────────────────────────────────────────────────

if (args.Length < 2 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Usage: GenerateTablebase <maxPieces> <outputDir>");
    Console.WriteLine();
    Console.WriteLine("  maxPieces   Maximum total piece count to generate (2–8).");
    Console.WriteLine("              Typical values: 6 (minutes), 5 (seconds), 4 (instant).");
    Console.WriteLine("  outputDir   Directory where .tb files are saved.");
    Console.WriteLine();
    Console.WriteLine("Examples:");
    Console.WriteLine("  GenerateTablebase 6 ./tablebase");
    Console.WriteLine("  GenerateTablebase 5 ./tablebase");
    return 0;
}

if (!int.TryParse(args[0], out int maxPieces) || maxPieces < 2 || maxPieces > 8)
{
    Console.Error.WriteLine($"Error: maxPieces must be an integer between 2 and 8. Got: '{args[0]}'");
    return 1;
}

string outputDir = args[1];

// ─── Progress reporting ───────────────────────────────────────────────────────

var totalSw = Stopwatch.StartNew();

var progress = new Progress<GenerationProgress>(p =>
{
    Console.WriteLine($"  [{p.ElapsedMs,6} ms]  {p.Config,-30}  {p.Positions,12:N0} positions");
});

// ─── Generation ───────────────────────────────────────────────────────────────

Console.WriteLine($"Generating tablebase for up to {maxPieces} pieces...");
Console.WriteLine($"Output directory: {Path.GetFullPath(outputDir)}");
Console.WriteLine();
Console.WriteLine($"  {"Elapsed",8}   {"Configuration",-30}  {"Positions",12}");
Console.WriteLine(new string('-', 62));

EndgameTablebase tb;
try
{
    tb = EndgameTablebase.Generate(maxPieces, progress);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Generation failed: {ex.Message}");
    return 2;
}

Console.WriteLine(new string('-', 62));
Console.WriteLine($"  Total configurations : {tb.ConfigCount:N0}");
Console.WriteLine($"  Total positions      : {tb.TotalPositions:N0}");
Console.WriteLine($"  Disk size (approx.)  : {tb.DiskBytes / 1024.0 / 1024.0:F1} MB");
Console.WriteLine($"  Wall time            : {totalSw.Elapsed.TotalSeconds:F1} s");
Console.WriteLine();

// ─── Save ─────────────────────────────────────────────────────────────────────

Console.Write($"Saving to '{outputDir}'... ");
try
{
    tb.Save(outputDir);
    Console.WriteLine("done.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to save: {ex.Message}");
    return 3;
}

// ─── Summary ──────────────────────────────────────────────────────────────────

Console.WriteLine();
Console.WriteLine("Configurations saved:");
foreach (var line in tb.ConfigSummary())
    Console.WriteLine($"  {line}");

Console.WriteLine();
Console.WriteLine("To use the tablebase with the engine:");
Console.WriteLine($"  var tb     = EndgameTablebase.Load(\"{outputDir}\");");
Console.WriteLine("  var engine = new BrazilianCheckersEngine(tablebase: tb);");
Console.WriteLine();
Console.WriteLine("Generation complete.");
return 0;
