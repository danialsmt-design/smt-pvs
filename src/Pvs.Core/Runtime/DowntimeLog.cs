namespace Pvs.Core.Runtime;

/// <summary>A span the line was down (no machine online). End is null while still down.</summary>
public sealed record DownSpan(DateTime Start, DateTime? End)
{
    public TimeSpan Duration(DateTime now) => (End ?? now) - Start;
}

/// <summary>
/// Records line up/down transitions from periodic samples of aggregate machine-online state, so a daily
/// report can show when and how long the line was stopped. Pure and in-memory (unit-testable) — the
/// sampling clock and persistence live in the host.
///
/// "Down" = no machine online. The first online sample marks <see cref="FirstUp"/>; a span opened before
/// that (the line simply not started yet) is excluded from production downtime, so pre-shift idle at boot
/// doesn't count. Once the line has come up, every later drop to zero-online is a counted stop.
/// </summary>
public sealed class DowntimeLog
{
    private readonly List<DownSpan> _spans = new();
    private bool _up;
    private bool _seeded;

    public DateTime? FirstUp { get; private set; }
    public IReadOnlyList<DownSpan> Spans => _spans;
    public bool IsUp => _up;
    public bool Seeded => _seeded;

    /// <summary>Restore persisted state (host reload).</summary>
    public void Restore(IEnumerable<DownSpan> spans, DateTime? firstUp, bool up, bool seeded)
    {
        _spans.Clear();
        _spans.AddRange(spans);
        FirstUp = firstUp;
        _up = up;
        _seeded = seeded;
    }

    /// <summary>Reset to a clean state (e.g. at a new day boundary).</summary>
    public void Clear()
    {
        _spans.Clear();
        FirstUp = null;
        _up = false;
        _seeded = false;
    }

    /// <summary>Feed one sample of whether ANY machine is online, at <paramref name="now"/>.</summary>
    public void Sample(bool anyOnline, DateTime now)
    {
        if (anyOnline && FirstUp is null) FirstUp = now;

        if (!_seeded)
        {
            _seeded = true;
            _up = anyOnline;
            if (!anyOnline) _spans.Add(new DownSpan(now, null));
            return;
        }

        if (anyOnline == _up) return;
        if (!anyOnline) _spans.Add(new DownSpan(now, null));   // up -> down: open a stop
        else CloseOpen(now);                                   // down -> up: close it
        _up = anyOnline;
    }

    private void CloseOpen(DateTime now)
    {
        for (int i = _spans.Count - 1; i >= 0; i--)
            if (_spans[i].End is null) { _spans[i] = _spans[i] with { End = now }; return; }
    }

    /// <summary>Down spans that count as production downtime (at/after the line first came up).</summary>
    public IReadOnlyList<DownSpan> ProductionSpans =>
        FirstUp is DateTime f ? _spans.Where(s => s.Start >= f).ToList() : new List<DownSpan>();

    /// <summary>Total production downtime as of <paramref name="now"/> (open span counted up to now).</summary>
    public TimeSpan TotalDown(DateTime now) =>
        ProductionSpans.Aggregate(TimeSpan.Zero, (a, s) => a + s.Duration(now));
}
