using Pvs.Core.Inventory;

namespace Pvs.LineApp.Runtime;

/// <summary>
/// Loads and saves the shortage monitor's state as JSON next to the app, the same way the other services
/// persist theirs. Every operation is best-effort: a missing, empty, truncated or hand-edited file yields a
/// FRESH state and a log line, never an exception. The parsing itself lives in
/// <see cref="ShortageStateCodec"/> so it is testable without touching a disk.
/// </summary>
public sealed class ShortageStateStore
{
    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _gate = new();

    public ShortageStateStore(string path, ILogger log) { _path = path; _log = log; }

    public string Path => _path;

    public ShortageMonitorState Load()
    {
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path)) return new ShortageMonitorState();
                var state = ShortageStateCodec.Read(File.ReadAllText(_path), out var problem);
                if (problem is not null)
                    _log.LogWarning("Shortage monitor state file {Path} is unusable ({Problem}) — starting fresh.", _path, problem);
                return state;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Shortage monitor state file {Path} could not be read — starting fresh.", _path);
            return new ShortageMonitorState();
        }
    }

    public void Save(ShortageMonitorState state)
    {
        try
        {
            lock (_gate)
            {
                // Write beside the target then replace, so a crash mid-write cannot leave half a file behind.
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, ShortageStateCodec.Write(state));
                File.Move(tmp, _path, overwrite: true);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Could not save shortage monitor state to {Path}.", _path); }
    }
}
