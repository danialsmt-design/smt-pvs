using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;
using Pvs.Core.People;

namespace Pvs.Data;

/// <summary>
/// Local, offline badge cache for <see cref="SqlReelPartRepository"/>. Every badge resolved while the DB is up is
/// remembered on disk (scanned UID → person + role), and a heartbeat preload keeps the whole roster cached. When
/// BOTH DB routes are down, <see cref="FindBadgeAsync"/> serves auth from here so an operator/supervisor can still
/// complete a parts-exchange — the count is kept locally and reconciled to the DB once it is reachable again.
/// </summary>
public sealed partial class SqlReelPartRepository
{
    private static string BadgeCachePath => System.IO.Path.Combine(AppContext.BaseDirectory, "badge-cache.json");
    private readonly ConcurrentDictionary<string, Badge> _badgeCache = new(StringComparer.OrdinalIgnoreCase);
    private int _badgeCacheLoaded;                 // 0 until first load (Interlocked one-shot)
    private readonly object _badgeSaveLock = new();

    /// <summary>Last badge-preload outcome, for the diagnostic endpoint.</summary>
    public string LastPreloadOutcome { get; private set; } = "not run";
    /// <summary>Count of badges currently in the offline cache.</summary>
    public int CachedBadgeCount { get { EnsureBadgeCacheLoaded(); return _badgeCache.Count; } }

    private static string BadgeKey(string? uid) => (uid ?? string.Empty).Trim();

    private void EnsureBadgeCacheLoaded()
    {
        if (System.Threading.Interlocked.Exchange(ref _badgeCacheLoaded, 1) == 1) return;
        try
        {
            if (System.IO.File.Exists(BadgeCachePath))
            {
                var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Badge>>(
                    System.IO.File.ReadAllText(BadgeCachePath));
                if (dict is not null)
                    foreach (var kv in dict)
                        if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null)
                            _badgeCache[BadgeKey(kv.Key)] = kv.Value;
            }
        }
        catch { /* corrupt/missing cache — start empty, it refills on the next successful lookup/preload */ }
    }

    private Badge? CachedBadge(string? uid)
    {
        EnsureBadgeCacheLoaded();
        return _badgeCache.TryGetValue(BadgeKey(uid), out var b) ? b : null;
    }

    private void CacheBadge(string? uid, Badge badge)
    {
        var key = BadgeKey(uid);
        if (key.Length == 0) return;
        _badgeCache[key] = badge;
        SaveBadgeCache();
    }

    private void SaveBadgeCache()
    {
        try
        {
            lock (_badgeSaveLock)
                System.IO.File.WriteAllText(BadgeCachePath,
                    System.Text.Json.JsonSerializer.Serialize(_badgeCache));
        }
        catch { /* best-effort; the in-memory cache still serves this session */ }
    }

    /// <summary>Preload EVERY badge into the offline cache (UID → person + role). Best-effort: if the DB is down it
    /// returns 0 and leaves the existing cache intact. Called on a heartbeat while the DB is reachable so the cache
    /// covers people who have not scanned yet this session.</summary>
    public async Task<int> PreloadBadgesAsync(CancellationToken ct = default)
    {
        EnsureBadgeCacheLoaded();
        const string sql =
            @"SELECT LTRIM(RTRIM(ISNULL(UserUID,''))) AS UserUID, LTRIM(RTRIM(ISNULL(UserID,''))) AS UserID,
                     LTRIM(RTRIM(ISNULL(UserName,''))) AS UserName, ISNULL(AccessLevel,'') AS AccessLevel
              FROM Users WHERE UserUID IS NOT NULL AND LTRIM(RTRIM(UserUID)) <> ''";
        try
        {
            int n = 0;
            await using var cn = await OpenAsync(ct);
            await using var cmd = new SqlCommand(sql, cn);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var uid = r.GetString(0);
                if (string.IsNullOrWhiteSpace(uid)) continue;
                _badgeCache[BadgeKey(uid)] = new Badge(r.GetString(1), r.GetString(2), r.GetString(3));
                n++;
            }
            if (n > 0) SaveBadgeCache();
            LastPreloadOutcome = $"cached {n} @ {DateTime.Now:HH:mm:ss}";
            return n;
        }
        catch (Exception ex) { LastPreloadOutcome = "error: " + ex.Message; return 0; }   // DB unreachable / query issue — keep existing cache
    }
}
