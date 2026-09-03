namespace Pvs.Core.Boards;

/// <summary>
/// A parsed second-side magazine slip (MCS "Production Identification Slip"). The QR string is
/// <c>PO: &lt;po&gt; | QTY: &lt;total&gt; | MAG: &lt;n&gt;/&lt;N&gt; | DATE: &lt;dd/mm/yyyy&gt;</c> (source of truth:
/// MCS <c>slip.html</c>). Per-magazine boards are NOT a field — they are DERIVED as <see cref="PerMagazine"/> =
/// total QTY ÷ N (Danial: "the qty is divided by number of card, reverse the total by number of card"). The
/// unique per-magazine key is (PO, MAG n) for dedupe.
/// </summary>
public sealed record MagazineSlip(string Po, int QtyTotal, int MagNo, int MagTotal, string Date)
{
    /// <summary>Boards in THIS magazine = the PO total divided by the number of cards.</summary>
    public int PerMagazine => MagTotal > 0 ? QtyTotal / MagTotal : 0;

    /// <summary>Dedupe key — a magazine can only be counted once for its PO.</summary>
    public string Key => Po + "|" + MagNo;
}

/// <summary>Tolerant parser for the magazine slip QR. Reads the <c>KEY: value</c> segments in any order and
/// ignores extra ones (so MCS can append fields without breaking PVS). Returns null for a non-magazine scan.</summary>
public static class MagazineSlipParser
{
    public static MagazineSlip? TryParse(string? qr)
    {
        if (string.IsNullOrWhiteSpace(qr)) return null;
        // Cheap gate: a magazine QR carries PO and MAG. A reel scan / part number won't.
        if (qr.IndexOf("MAG", StringComparison.OrdinalIgnoreCase) < 0 ||
            qr.IndexOf("PO", StringComparison.OrdinalIgnoreCase) < 0) return null;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var seg in qr.Split('|'))
        {
            int c = seg.IndexOf(':');
            if (c <= 0) continue;
            var k = seg[..c].Trim();
            var v = seg[(c + 1)..].Trim();
            if (k.Length > 0) map[k] = v;
        }

        if (!map.TryGetValue("PO", out var po) || string.IsNullOrWhiteSpace(po)) return null;
        if (!map.TryGetValue("MAG", out var mag)) return null;
        var mp = mag.Split('/');
        if (mp.Length != 2 ||
            !int.TryParse(mp[0].Trim(), out var n) || n <= 0 ||
            !int.TryParse(mp[1].Trim(), out var total) || total <= 0) return null;

        int qty = 0;
        if (map.TryGetValue("QTY", out var qs))
        {
            var digits = new string(qs.Where(char.IsDigit).ToArray());   // tolerate "1,800" / "1800 PCS"
            int.TryParse(digits, out qty);
        }
        string date = map.TryGetValue("DATE", out var d) ? d.Trim() : "";
        return new MagazineSlip(po.Trim(), qty, n, total, date);
    }
}
