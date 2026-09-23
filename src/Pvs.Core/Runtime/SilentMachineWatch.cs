namespace Pvs.Core.Runtime;

public enum SilentVerdict { Ok, WentSilent, StillSilent, Recovered, NotEligible }

/// <summary>
/// Detects a machine whose board-complete stream has stopped while the LINE keeps completing panels. Sampled on a
/// slow tick: between two samples the line clock advanced by at least <see cref="MinGapPanels"/> and the machine's
/// own tally did not move at all → silent. Recovers the moment its tally moves again.
/// </summary>
public sealed class SilentMachineWatch
{
    public const int MinGapPanels = 10;
    private readonly Dictionary<int, (long Clock, int Tally)> _last = new();
    private readonly Dictionary<int, (long GapPanels, DateTime Since)> _silent = new();
    private readonly object _lock = new();

    public IReadOnlyDictionary<int, (long GapPanels, DateTime Since)> Silent { get { lock (_lock) return new Dictionary<int, (long, DateTime)>(_silent); } }

    public SilentVerdict Observe(int machine, long lineClock, int machineTally, bool eligible, DateTime now)
    {
        lock (_lock)
        {
            if (!eligible) { _last.Remove(machine); _silent.Remove(machine); return SilentVerdict.NotEligible; }
            if (!_last.TryGetValue(machine, out var prev)) { _last[machine] = (lineClock, machineTally); return SilentVerdict.Ok; }
            long gap = lineClock - prev.Clock;
            bool moved = machineTally != prev.Tally;
            _last[machine] = (lineClock, machineTally);
            if (moved)
            {
                if (_silent.Remove(machine)) return SilentVerdict.Recovered;
                return SilentVerdict.Ok;
            }
            if (gap >= MinGapPanels)
            {
                if (_silent.TryGetValue(machine, out var s)) { _silent[machine] = (s.GapPanels + gap, s.Since); return SilentVerdict.StillSilent; }
                _silent[machine] = (gap, now);
                return SilentVerdict.WentSilent;
            }
            return _silent.ContainsKey(machine) ? SilentVerdict.StillSilent : SilentVerdict.Ok;
        }
    }
}
