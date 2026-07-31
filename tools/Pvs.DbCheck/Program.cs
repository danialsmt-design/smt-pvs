using Pvs.Core.Data;
using Pvs.Data;

// Exercises the read-only data layer against live ReelPart-New and prints what it reads,
// so the queries + mapping can be validated end-to-end. Read-only; writes nothing.

string cs = args.Length > 0
    ? args[0]
    : @"Server=.\SQLEXPRESS;Database=ReelPart-New;Integrated Security=True;TrustServerCertificate=True;Connect Timeout=15";

IReelPartRepository repo = new SqlReelPartRepository(cs);

try
{
    var products = await repo.GetProductsAsync();
    Console.WriteLine($"[Products] {products.Count} models");
    foreach (var p in products.Take(4)) Console.WriteLine($"    {p.ProductId,2}  {p.Name}");

    var l261 = products.FirstOrDefault(p => p.Name == "L261");
    if (l261 is not null)
    {
        var map = await repo.GetFeederMapAsync(l261.ProductId, "B", 0); // 0 = all lines (diagnostic)
        int assigned = map.Count(f => f.Position.IsAssigned);
        Console.WriteLine($"\n[FeederMap] L261 (id {l261.ProductId}) B-side (all lines): {map.Count} rows, {assigned} with a feeder position");
        foreach (var f in map.Where(f => f.Position.IsAssigned).Take(5))
            Console.WriteLine($"    M{f.Machine}  feeder {f.Position.Number}/{f.Position.Side}  {f.PartNumber}");
    }

    var badge = await repo.FindBadgeAsync("4031-E222%");
    Console.WriteLine($"\n[Badge] 4031-E222% -> {badge?.Name} / {badge?.Role} / canRelease={badge?.CanReleaseInterlock}");

    var missing = await repo.FindBadgeAsync("does-not-exist");
    Console.WriteLine($"[Badge] unknown UID -> {(missing is null ? "null (correct)" : "UNEXPECTED HIT")}");

    var reel = await repo.FindReelAsync("3008-B331%", "VS1-9243-008");
    Console.WriteLine($"\n[Reel] 3008-B331% / VS1-9243-008 -> remaining={reel?.RemainingQty}, active={reel?.Active}");

    Console.WriteLine("\nDATA LAYER OK");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine("DATA LAYER FAILED: " + ex.Message);
    return 1;
}
