using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Estadisticas.Services;

/// <summary>
/// Simple in-memory cache for query results. TTL is chosen per window:
/// - 2w:       1 hour  (changes when the current week ends — Sunday→Monday)
/// - 1m-12m:   1 hour  (changes when the current month ends)
/// - last_year: 1 hour (changes when the current year ends)
///
/// In practice all windows only change at month boundaries (except 2w which
/// changes at week boundaries), so a 1-hour TTL is safe and keeps the cache
/// fresh enough. The real benefit is avoiding repeated heavy SQL queries
/// within the same browsing session.
///
/// Cache keys are strings like "Query:Songs:Top:12m:50:1982".
/// </summary>
public sealed class QueryCache
{
    private readonly Dictionary<string, CacheEntry> _entries = new();
    private readonly object _lock = new();

    /// <summary>Default TTL for all cached entries.</summary>
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(1);

    /// <summary>Get a cached value, or default(T) if expired/missing.</summary>
    public T? Get<T>(string key) where T : class
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out var entry))
            {
                if (DateTime.UtcNow < entry.ExpiresAt)
                {
                    return entry.Value as T;
                }
                _entries.Remove(key);
            }
            return null;
        }
    }

    /// <summary>Store a value in the cache with the default TTL.</summary>
    public void Set<T>(string key, T value) where T : class
    {
        lock (_lock)
        {
            _entries[key] = new CacheEntry
            {
                Value = value,
                ExpiresAt = DateTime.UtcNow.Add(DefaultTtl)
            };
        }
    }

    /// <summary>Clear all cached entries. Call when data changes (e.g. after import or purge).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }
    }

    /// <summary>Get or set pattern: returns cached value if valid, otherwise calls factory, caches, and returns.</summary>
    public T GetOrSet<T>(string key, Func<T> factory) where T : class
    {
        var cached = Get<T>(key);
        if (cached != null) return cached;
        var value = factory();
        Set(key, value);
        return value;
    }

    private sealed class CacheEntry
    {
        public object? Value { get; set; }
        public DateTime ExpiresAt { get; set; }
    }
}
