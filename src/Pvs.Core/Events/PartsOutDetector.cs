using Pvs.Core.Serial;

namespace Pvs.Core.Events;

/// <summary>A confirmed, de-duplicated parts-out that should prompt the operator.</summary>
public readonly record struct PartsOutEvent(int Machine, int Feeder, int Step, DateTime At);

/// <summary>
/// Turns the raw R2 message stream from ONE machine into confirmed parts-out events.
///
/// Grounded in real captured behaviour (2026-07-22/23):
///  - A genuine exhaustion emits a BURST: R2E02 -> R2E02 -> R2E04 -> R2E03, same feeder + step,
///    all within ~4 s. We must raise exactly ONE prompt per burst.
///  - We alert ONLY on R2E03 (the machine actually stopped, out of parts). Pickup errors (R2E02)
///    ran 17x more frequently than genuine parts-outs; alerting on them would train operators to
///    ignore the system. The E02/E04 members of a burst therefore never alert on their own.
///  - Tray faults loop R2E02/R2E08 and NEVER reach R2E03, so they correctly raise nothing here.
///  - The same feeder can genuinely exhaust again minutes later (feeder 123 went 5x in a shift);
///    those are distinct events. So the de-dupe window is short (default 5 s) — long enough to
///    swallow one burst, far shorter than a real refill cycle.
///
/// One detector instance per machine/port.
/// </summary>
public sealed class PartsOutDetector
{
    private readonly int _machine;
    private readonly TimeSpan _dedupeWindow;
    // last R2E03 we already reported, per feeder
    private readonly Dictionary<int, DateTime> _lastReported = new();

    public PartsOutDetector(int machine, TimeSpan? dedupeWindow = null)
    {
        _machine = machine;
        _dedupeWindow = dedupeWindow ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Feeds one decoded message with its arrival time. Returns a PartsOutEvent to raise,
    /// or null if this message is not a fresh parts-out (a burst member, a jam, a duplicate,
    /// or anything unrelated).
    /// </summary>
    public PartsOutEvent? Observe(SonyMessage message, DateTime at)
    {
        // Only a genuine parts-out (machine stopped, out of parts) raises a prompt.
        if (message.Kind != MessageKind.PartsOut || message.Feeder is not int feeder)
            return null;

        if (_lastReported.TryGetValue(feeder, out var previous) &&
            at - previous <= _dedupeWindow)
        {
            // Same feeder within the burst window -> already reported this exhaustion.
            _lastReported[feeder] = at;
            return null;
        }

        _lastReported[feeder] = at;
        return new PartsOutEvent(_machine, feeder, message.Step ?? 0, at);
    }
}
