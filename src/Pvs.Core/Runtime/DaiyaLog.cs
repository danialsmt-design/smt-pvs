namespace Pvs.Core.Runtime;

/// <summary>A person who scanned a badge on the line this shift (for the Daiya Graph name fields).</summary>
public sealed record DaiyaOperator(string UserId, string Name, int Level, DateTime At);

/// <summary>
/// Collects the live data the Daiya Graph (GMS-QP-15F06) needs that PVS does not already hold elsewhere:
///   • line-out boards bucketed per clock hour (the "Hourly Output" panel/pcs grid),
///   • the shift's first and last board (PROD START / END TIME),
///   • the roster of people who scanned a badge this shift (the operator/line-leader name fields).
/// Everything else on the form (model, PO, lot, target, total, downtime) comes from the coordinator / downtime
/// capture at assembly time. Pure and in-memory; rolls at the day boundary. READ-ONLY — a report input only.
/// </summary>
public sealed class DaiyaLog
{
    private readonly object _gate = new();
    private string _date = DateTime.Now.ToString("yyyy-MM-dd");
    private readonly Dictionary<int, int> _panelByHour = new();      // :30-aligned slot hour (0-23) -> panels
    private readonly Dictionary<string, DaiyaOperator> _ops = new(); // by UserId
    private DateTime? _first, _last;
    private long _panels;

    private void RollIfNeeded(DateTime at)
    {
        var d = at.ToString("yyyy-MM-dd");
        if (d == _date) return;
        _panelByHour.Clear(); _ops.Clear(); _first = _last = null; _panels = 0; _date = d;
    }

    /// <summary>The :30-aligned slot a time falls in (e.g. 17:45 -> 17; 17:20 -> 16), matching the form's
    /// "8.30am / 9.30am / …" columns.</summary>
    public static int SlotHour(DateTime at) => at.Minute >= 30 ? at.Hour : (at.Hour + 23) % 24;

    /// <summary>One line-out board completed (last machine / M4). <paramref name="panels"/> is normally 1.</summary>
    public void OnBoard(int panels, DateTime at)
    {
        if (panels <= 0) return;
        lock (_gate)
        {
            RollIfNeeded(at);
            int slot = SlotHour(at);
            _panelByHour[slot] = (_panelByHour.TryGetValue(slot, out var v) ? v : 0) + panels;
            _panels += panels;
            _first ??= at;
            _last = at;
        }
    }

    /// <summary>Record a person who scanned a badge this shift (found badges only). Dedupes by user.</summary>
    public void AddOperator(string? userId, string? name, int level, DateTime at)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId == "?" || string.IsNullOrWhiteSpace(name)) return;
        lock (_gate)
        {
            RollIfNeeded(at);
            _ops[userId!] = new DaiyaOperator(userId!, name!.Trim(), level, at);
        }
    }

    public DateTime? FirstBoard { get { lock (_gate) return _first; } }
    public DateTime? LastBoard { get { lock (_gate) return _last; } }
    public long PanelsTotal { get { lock (_gate) return _panels; } }

    /// <summary>Panels per :30-aligned slot hour (copy).</summary>
    public IReadOnlyDictionary<int, int> PanelByHour() { lock (_gate) return new Dictionary<int, int>(_panelByHour); }

    /// <summary>Everyone who scanned this shift — highest authority first (line-leader = the top level), then by time.</summary>
    public IReadOnlyList<DaiyaOperator> Operators() { lock (_gate) return _ops.Values.OrderByDescending(o => o.Level).ThenBy(o => o.At).ToList(); }

    public void Clear() { lock (_gate) { _panelByHour.Clear(); _ops.Clear(); _first = _last = null; _panels = 0; } }
}
