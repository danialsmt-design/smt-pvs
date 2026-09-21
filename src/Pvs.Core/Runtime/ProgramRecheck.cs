namespace Pvs.Core.Runtime;

/// <summary>Helpers for the program file name a machine reports over C3P (e.g. "L307 - B SIDE _Cell4.PW4").</summary>
public static class ProgramNames
{
    /// <summary>Leading model token of a program name: "L307 - B SIDE _Cell4.PW4" -> "L307". Empty when unknown.</summary>
    public static string ModelOf(string? program)
    {
        var m = System.Text.RegularExpressions.Regex.Match(program ?? "", @"^\s*([A-Za-z0-9]+)");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
    }
}

/// <summary>
/// PVS caches each machine's program name and normally never re-asks (C3P is housekeeping that steps on report
/// reads). That cache can go STALE: on 2026-09-21 L1 M4 was changed L313 -> L307 without PVS seeing the machine
/// come on-line and its C3P had been refused, so PVS flashed "M4:L313 vs L307" while the HMI showed L307.
/// Rule: a program MISMATCH is only believed after the machines have been RE-ASKED. While the cached names
/// disagree, every machine in the comparison is re-asked, throttled so it never floods the serial line.
/// </summary>
public sealed class ProgramRecheck
{
    private readonly Dictionary<int, DateTime> _lastAsk = new();
    private readonly object _lock = new();

    /// <summary>Gap between re-asks of one machine while it is stopped (a C3P is answered at once).</summary>
    public TimeSpan StoppedInterval { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Gap while it is in AUTO production (the machine may refuse with A4E00, which pollutes report reads).</summary>
    public TimeSpan RunningInterval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>The machines whose cached program names DISAGREE on the model — the same set the mismatch alarm
    /// compares (online, not skipped, name known). Empty when they all agree or fewer than two have a name.
    /// ALL of them are returned, not just the minority: the stale name can be on either side.</summary>
    public static IReadOnlyList<int> Disagreeing(IEnumerable<(int Machine, string? Program, bool Online, bool Skipped)> machines)
    {
        var set = machines.Where(m => m.Online && !m.Skipped)
            .Select(m => (m.Machine, Model: ProgramNames.ModelOf(m.Program)))
            .Where(m => m.Model.Length > 0).ToList();
        return set.Select(m => m.Model).Distinct().Count() > 1
            ? set.Select(m => m.Machine).OrderBy(x => x).ToList()
            : Array.Empty<int>();
    }

    /// <summary>True when this machine may be re-asked now (never asked, or its interval has passed).</summary>
    public bool Due(int machine, DateTime now, bool running)
    {
        lock (_lock)
            return !_lastAsk.TryGetValue(machine, out var last) || now - last >= (running ? RunningInterval : StoppedInterval);
    }

    /// <summary>Record that the C3P was actually SENT (not when it was held back by a report read).</summary>
    public void MarkAsked(int machine, DateTime now) { lock (_lock) _lastAsk[machine] = now; }
}
