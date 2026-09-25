namespace Pvs.Core.Feeders;

/// <summary>One feeder row of the Feeder Master: what the machine places from this feeder on ONE child board.</summary>
public sealed record MasterFeeder(int Feeder, string Part, int ShotsPerBoard);

/// <summary>
/// The FEEDER MASTER for one model + side on one line — Danial 2026-09-25: the single source for the reel count-down,
/// "no other source is allowed". Shots are PER BOARD, whole numbers (the product qualifies a fixed placement count);
/// per panel is always shots × BoardsPerPanel from the same block. The DB feeder map and a pen-drive file are only
/// ways to FILL a block; the count-down never reads them directly.
/// </summary>
public sealed record MasterBlock(
    string Model,
    string Side,
    int BoardsPerPanel,
    Dictionary<int, List<MasterFeeder>> Machines,   // machine number -> feeders
    string Source,                                  // "db" | "pendrive" | "edited"
    bool Reviewed,                                  // a supervisor has looked at it and saved it
    DateTime UpdatedAt,
    string UpdatedBy,
    int Version)
{
    public static string KeyOf(string model, string side) => $"{model.Trim().ToUpperInvariant()}|{(side ?? "A").Trim().ToUpperInvariant()}";
    public string Key => KeyOf(Model, Side);

    public int MachineShotsPerBoard(int machine) => Machines.TryGetValue(machine, out var l) ? l.Sum(f => f.ShotsPerBoard) : 0;
    public int MachineShotsPerPanel(int machine) => MachineShotsPerBoard(machine) * Math.Max(1, BoardsPerPanel);
    public int TotalShotsPerBoard => Machines.Values.Sum(l => l.Sum(f => f.ShotsPerBoard));
    public int TotalShotsPerPanel => TotalShotsPerBoard * Math.Max(1, BoardsPerPanel);
    public int FeederCount => Machines.Values.Sum(l => l.Count);
}

public static class FeederMasterRules
{
    /// <summary>Validate a block before it is saved. Returns the problems found (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(MasterBlock b)
    {
        var errs = new List<string>();
        if (string.IsNullOrWhiteSpace(b.Model)) errs.Add("model is empty");
        if (b.Side is not ("A" or "B" or "a" or "b")) errs.Add($"side must be A or B (got '{b.Side}')");
        if (b.BoardsPerPanel < 1 || b.BoardsPerPanel > 20) errs.Add($"boards per panel must be 1..20 (got {b.BoardsPerPanel})");
        if (b.Machines.Count == 0 || b.FeederCount == 0) errs.Add("no feeders");
        foreach (var (m, list) in b.Machines)
        {
            if (m < 1 || m > 8) errs.Add($"machine {m} out of range");
            var seen = new HashSet<int>();
            foreach (var f in list)
            {
                if (f.Feeder <= 0) errs.Add($"M{m}: feeder number {f.Feeder} invalid");
                if (!seen.Add(f.Feeder)) errs.Add($"M{m}: feeder {f.Feeder} listed twice");
                if (string.IsNullOrWhiteSpace(f.Part)) errs.Add($"M{m} F{f.Feeder}: part is empty");
                if (f.ShotsPerBoard < 1) errs.Add($"M{m} F{f.Feeder}: shots per board must be a whole number ≥ 1 (got {f.ShotsPerBoard})");
            }
        }
        return errs;
    }

    /// <summary>A pen-drive count is PER PANEL; it must divide by boards-per-panel to a whole number of shots per
    /// board — "you can't break a component in half". Null when it does not divide.</summary>
    public static int? ShotsPerBoardFromPanelCount(int perPanel, int boardsPerPanel)
    {
        if (perPanel <= 0 || boardsPerPanel <= 0) return null;
        return perPanel % boardsPerPanel == 0 ? perPanel / boardsPerPanel : null;
    }

    /// <summary>Row-by-row difference between two blocks, for the import preview.</summary>
    public static IReadOnlyList<string> Diff(MasterBlock? current, MasterBlock incoming)
    {
        var changes = new List<string>();
        if (current is null) { changes.Add($"new block: {incoming.FeederCount} feeders, {incoming.TotalShotsPerBoard} shots/board"); return changes; }
        if (current.BoardsPerPanel != incoming.BoardsPerPanel) changes.Add($"boards per panel {current.BoardsPerPanel} -> {incoming.BoardsPerPanel}");
        var cur = current.Machines.SelectMany(kv => kv.Value.Select(f => ((kv.Key, f.Feeder), f))).ToDictionary(x => x.Item1, x => x.f);
        var inc = incoming.Machines.SelectMany(kv => kv.Value.Select(f => ((kv.Key, f.Feeder), f))).ToDictionary(x => x.Item1, x => x.f);
        foreach (var (k, f) in inc)
        {
            if (!cur.TryGetValue(k, out var c)) { changes.Add($"M{k.Item1} F{k.Item2}: added {f.Part} × {f.ShotsPerBoard}"); continue; }
            if (!string.Equals(c.Part, f.Part, StringComparison.OrdinalIgnoreCase)) changes.Add($"M{k.Item1} F{k.Item2}: part {c.Part} -> {f.Part}");
            if (c.ShotsPerBoard != f.ShotsPerBoard) changes.Add($"M{k.Item1} F{k.Item2} {f.Part}: shots {c.ShotsPerBoard} -> {f.ShotsPerBoard}");
        }
        foreach (var k in cur.Keys) if (!inc.ContainsKey(k)) changes.Add($"M{k.Item1} F{k.Item2}: removed ({cur[k].Part})");
        return changes;
    }
}
