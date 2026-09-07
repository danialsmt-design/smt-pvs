namespace Pvs.Core.Requests;

/// <summary>One feeder row as the exhaust forecast reports it (the subset the planner needs).</summary>
public sealed record ExhaustRow(
    int Machine, int Feeder, string Part, int Remaining, double? Minutes,
    bool NeedsRequest, bool SpareReady, bool LastsLot, int Issued, int? NeededLot);

/// <summary>A parts request the line wants the store to fulfil (and bring by robot).</summary>
public sealed record RequestToOpen(string Part, int Machine, int Feeder, double MinutesLeft, int PiecesNeeded, int Remaining);

/// <summary>An open request that no longer applies (reel loaded, spare staged, lot changed, feeder gone).</summary>
public sealed record RequestToClose(string Part, string Reason);

public sealed record RequestPlan(IReadOnlyList<RequestToOpen> Open, IReadOnlyList<RequestToClose> Close);

/// <summary>
/// Decides, from the exhaust forecast, which parts the line should ask the store for NOW and which open requests
/// have been satisfied. Pure: no clock, no I/O. The service around it debounces the close decision (two polls)
/// so a single noisy read cannot cancel a request the store is already picking.
/// <para>Open when: the forecast says the feeder needs material (won't last the lot, no spare staged, lot
/// under-issued), its run-out is within <paramref name="thresholdMinutes"/>, and no request for that part is open.
/// One request per part per line — several feeders of the same part share it.</para>
/// <para>Close when: the part no longer has any feeder that needs material (loaded / spare staged / lasts the lot),
/// or the part is no longer on the line at all.</para>
/// </summary>
public static class PartsRequestPlanner
{
    public static RequestPlan Plan(IReadOnlyList<ExhaustRow> rows, IReadOnlyCollection<string> openParts, double thresholdMinutes)
    {
        var open = new List<RequestToOpen>();
        var close = new List<RequestToClose>();
        var openSet = new HashSet<string>(openParts.Select(Norm), StringComparer.OrdinalIgnoreCase);

        var byPart = rows.Where(r => !string.IsNullOrWhiteSpace(r.Part))
                         .GroupBy(r => Norm(r.Part), StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (part, feeders) in byPart)
        {
            // the soonest feeder of this part that actually needs material and has a known run-out time
            var due = feeders.Where(f => f.NeedsRequest && f.Minutes is double m && m <= thresholdMinutes)
                             .OrderBy(f => f.Minutes).FirstOrDefault();
            if (due is not null && !openSet.Contains(part))
            {
                int pieces = due.NeededLot is int nl ? Math.Max(nl - due.Issued, 0) : 0;
                open.Add(new RequestToOpen(part, due.Machine, due.Feeder, due.Minutes!.Value, pieces, due.Remaining));
            }
        }

        foreach (var part in openSet)
        {
            if (!byPart.TryGetValue(part, out var feeders))
            {
                close.Add(new RequestToClose(part, "part no longer on the line"));
                continue;
            }
            if (feeders.Any(f => f.NeedsRequest)) continue;      // still needed
            string reason = feeders.Any(f => f.SpareReady) ? "spare reel staged at the line"
                          : feeders.All(f => f.LastsLot) ? "reel now lasts the lot"
                          : "material covered (reel loaded)";
            close.Add(new RequestToClose(part, reason));
        }

        return new RequestPlan(open, close);
    }

    private static string Norm(string s) => (s ?? "").Trim();
}
