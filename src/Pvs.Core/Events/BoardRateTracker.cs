using Pvs.Core.Serial;

namespace Pvs.Core.Events;

/// <summary>
/// Estimates a machine's current board rate (boards per hour) from its real-time stream.
///
/// The overnight capture proved this must be done carefully: board intervals swung from 84 s to
/// 762 s purely because the line kept stopping, and there was a 10.6 h overnight gap where the
/// line was simply off. Averaging wall-clock time between boards would make a stopping line look
/// slow and stretch every exhaust forecast. So we measure rate over PRODUCTIVE time only —
/// the clock advances between R1ST (auto started) and R1SP (auto stopped), and is frozen while
/// the machine is stopped, off-line, or overnight.
///
/// A rolling window of recent boards is kept so the rate reflects the current job, not the whole
/// day. Rate = boards in window / productive hours elapsed across those same boards.
/// </summary>
public sealed class BoardRateTracker
{
    private readonly int _windowSize;
    private bool _running;
    private DateTime _lastMark;                 // when productive time last started/resumed
    private double _productiveSeconds;          // total productive seconds accrued
    // productive-seconds timestamp of each recent board completion
    private readonly Queue<double> _boardMarks = new();

    /// <param name="windowSize">How many recent boards to average over (default 20).</param>
    public BoardRateTracker(int windowSize = 20)
    {
        _windowSize = Math.Max(2, windowSize);
    }

    /// <summary>Productive seconds accrued up to <paramref name="now"/> (advances only while running).</summary>
    private double ProductiveSecondsAt(DateTime now)
        => _running ? _productiveSeconds + (now - _lastMark).TotalSeconds : _productiveSeconds;

    /// <summary>Feed one decoded message with its arrival time.</summary>
    public void Observe(SonyMessage message, DateTime at)
    {
        switch (message.Kind)
        {
            case MessageKind.Status when message.StatusCode is "ST":   // auto operation started
                StartRunning(at);
                break;

            // Auto stopped, entered emergency stop, or left AUTO -> productive clock freezes.
            case MessageKind.Status when message.StatusCode is "SP" or "ES" or "MA" or "FL":
                StopRunning(at);
                break;

            case MessageKind.BoardComplete:
                // A board can complete right as the machine reports; make sure the clock is live.
                if (!_running) StartRunning(at);
                _boardMarks.Enqueue(ProductiveSecondsAt(at));
                while (_boardMarks.Count > _windowSize) _boardMarks.Dequeue();
                break;
        }
    }

    private void StartRunning(DateTime at)
    {
        if (_running) return;
        _running = true;
        _lastMark = at;
    }

    private void StopRunning(DateTime at)
    {
        if (!_running) return;
        _productiveSeconds += (at - _lastMark).TotalSeconds;
        _running = false;
    }

    /// <summary>
    /// Current boards-per-hour, or null until at least two boards have been seen.
    /// Measured across productive time only.
    /// </summary>
    public double? BoardsPerHour
    {
        get
        {
            if (_boardMarks.Count < 2) return null;
            double first = _boardMarks.Peek();
            double last = _boardMarks.Last();
            double productive = last - first;
            if (productive <= 0) return null;
            int intervals = _boardMarks.Count - 1;
            return intervals / productive * 3600.0;
        }
    }
}
