using System.Collections.Concurrent;

namespace Pvs.Core.Persistence;

/// <summary>
/// Crash-safe small-file persistence for the line PC's state files (a power cut mid-write used to leave a
/// truncated JSON that a Load* silently treated as "start clean" — zeroing the M4 total / lot count; restart audit
/// 2026-09-11). Write = temp file + atomic replace, keeping the previous good file as ".bak"; Load = the file,
/// else the ".bak". One lock per path so a serial-thread save and an HTTP-thread save never collide.
/// </summary>
public static class AtomicFile
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static object LockFor(string path) => Locks.GetOrAdd(Path.GetFullPath(path), _ => new object());

    /// <summary>Write <paramref name="text"/> to <paramref name="path"/> atomically; the previous file becomes path.bak.</summary>
    public static void Write(string path, string text)
    {
        lock (LockFor(path))
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
    }

    /// <summary>Parse the file with <paramref name="parse"/>; when it is missing, empty, corrupt or parses to null,
    /// fall back to the ".bak" written by the previous <see cref="Write"/>. Returns default when neither works.</summary>
    public static T? Load<T>(string path, Func<string, T?> parse)
    {
        lock (LockFor(path))
        {
            foreach (var p in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var text = File.ReadAllText(p);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var v = parse(text);
                    if (v is not null) return v;
                }
                catch { /* try the next candidate */ }
            }
            return default;
        }
    }

    /// <summary>Delete the file and its backup (state cleared on purpose).</summary>
    public static void Delete(string path)
    {
        lock (LockFor(path))
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            try { if (File.Exists(path + ".bak")) File.Delete(path + ".bak"); } catch { }
        }
    }
}
