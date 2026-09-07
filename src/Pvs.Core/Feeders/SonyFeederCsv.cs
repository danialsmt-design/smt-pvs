namespace Pvs.Core.Feeders;

/// <summary>
/// Parses a machine feeder-list CSV that a supervisor loads from a pen drive when a breakdown reshuffles the
/// feeders. Handles BOTH exports seen on the floor (auto-detected per row):
///
///   Sony  — a ';'-prefixed header block, then data rows: <c>Cell#, [F]108 (F), PartCode, ...</c>
///           (the Cell# is in the file; used to validate against the chosen machine).
///   JUKI RS-1 — plain title lines, header <c>FEEDER NO,PARTS NAME,QTY,FEEDER TYPE</c>, then data rows:
///           <c>F11, VE3-1480-104, 84, 8mm</c>  (NO Cell# column — the cell comes from the chosen machine).
///
/// Feeder, part AND the placement count are used: the Sony "Mount Step" column / the JUKI "QTY" column is how
/// many pieces that feeder places per board — the number the live decrement needs (a row without it decremented
/// at 1 per board, which on L1 2026-09-07 left reels running out while PVS showed thousands). The caller always
/// supplies the machine; a Sony file's Cell# is used only to catch "wrong file for this cell".
/// </summary>
public static class SonyFeederCsv
{
    /// <summary><paramref name="Qty"/> = placements per board from the file (Sony Mount Step / JUKI QTY); 0 when the
    /// file carries none — the loader rejects such a file unless a supervisor forces it.</summary>
    public readonly record struct Entry(int Machine, int Feeder, string Part, int Qty = 0);

    /// <summary>(feeder, part) for every data row, tagged with the caller's <paramref name="machine"/>.</summary>
    public static IReadOnlyList<Entry> Parse(string? content, int machine)
    {
        var rows = new List<Entry>();
        if (string.IsNullOrWhiteSpace(content)) return rows;
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;   // Sony comment/header block
            if (Row(line.Split(',')) is (int f, string p, int q)) rows.Add(new Entry(machine, f, p, q));
        }
        return rows;
    }

    // One data row -> (feeder, part, qty), or null for a header/blank row (either format). qty = 0 when absent.
    private static (int Feeder, string Part, int Qty)? Row(string[] cols)
    {
        if (cols.Length < 2) return null;
        var c0 = cols[0].Trim();
        if (c0.Length == 0) return null;
        // Sony: col0 = numeric Cell#, col1 = supply position ("[F]108 (F)"), col2 = part code, col4 = Mount Step
        // (placements per board; ";Cell #,Supply Pos.,Part Code,Polarity,Mount Step,..." header).
        if (int.TryParse(c0, out _) && cols.Length >= 3)
        {
            var pos = SupplyPosition.Parse(cols[1].Trim());
            var part = cols[2].Trim();
            int qty = cols.Length >= 5 && int.TryParse(cols[4].Trim(), out int ms) && ms > 0 ? ms : 0;
            return pos.Number is int f && part.Length > 0 ? (f, part, qty) : null;
        }
        // JUKI RS-1: col0 = a compact feeder token ("F11" / "Z8"); the part code is the first FOLLOWING non-empty
        // cell that looks like a part. Exports vary wildly — the RS-1 puts the part in col1, but the "FEEDER LIST
        // CANON" export pads with many empty columns and puts the part in col3+ (and uses "Z"-prefixed feeders).
        // Title/header rows have a space in col0 ("FEEDER NO", "PROGRAM NAME : ...") and are skipped.
        if (c0.Contains(' ')) return null;
        if (SupplyPosition.Parse(c0).Number is not int jf) return null;
        for (int i = 1; i < cols.Length; i++)
        {
            var cell = cols[i].Trim();
            if (cell.Length > 0 && LooksLikePart(cell))
            {
                // QTY = the first whole number in a later cell (the RS-1 puts it right after the part; the CANON
                // export pads with empty columns first).
                int qty = 0;
                for (int j = i + 1; j < cols.Length; j++)
                {
                    var c = cols[j].Trim();
                    if (c.Length == 0) continue;
                    if (int.TryParse(c, out int q) && q > 0) qty = q;
                    break;
                }
                return (jf, cell, qty);
            }
        }
        return null;
    }

    /// <summary>The single Cell# declared in a Sony file's data rows (to validate against the chosen machine),
    /// or null when the file carries no Cell# (JUKI) or mixes cells.</summary>
    public static int? DeclaredCell(string? content)
    {
        if (content is null) return null;
        var cells = new HashSet<int>();
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";")) continue;
            var cols = line.Split(',');
            if (cols.Length >= 3 && int.TryParse(cols[0].Trim(), out int cell) && SupplyPosition.Parse(cols[1].Trim()).Number.HasValue)
                cells.Add(cell);
        }
        return cells.Count == 1 ? cells.First() : (int?)null;
    }

    /// <summary>A short label for display — the Sony ";Comment,…" or the JUKI "PROGRAM NAME : …" line.</summary>
    public static string? Comment(string? content)
    {
        if (content is null) return null;
        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith(";Comment,", StringComparison.OrdinalIgnoreCase))
                return l.Substring(";Comment,".Length).Trim().TrimEnd(',').Trim();
            int pn = l.IndexOf("PROGRAM NAME", StringComparison.OrdinalIgnoreCase);
            if (pn >= 0)
            {
                // "PROGRAM NAME : L254,,,,,MODEL NAME : ..." -> "L254" (cut at the first column separator).
                var v = l.Substring(pn + "PROGRAM NAME".Length).TrimStart(' ', ':').Split(',')[0].Trim();
                if (v.Length > 0) return v;
            }
        }
        return null;
    }

    // A part number: has a digit, no spaces, not the literal "PARTS NAME" header cell.
    private static bool LooksLikePart(string s) =>
        s.Length >= 4 && s.Any(char.IsDigit) && !s.Contains(' ') &&
        !s.Equals("PARTS NAME", StringComparison.OrdinalIgnoreCase);
}
