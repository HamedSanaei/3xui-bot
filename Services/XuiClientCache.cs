using Microsoft.Extensions.Caching.Memory;

/// <summary>Ten-second cache of immutable foreground XUI read JSON, bounded by payload bytes.</summary>
/// <remarks>Never caches recovery, payment or mutation reads. Every consumer deserializes its own detached objects.
/// Invalidating the cache at both mutation boundaries prevents an in-flight pre-mutation read repopulating it.</remarks>
internal static class XuiClientCache
{
    private static readonly MemoryCache Cache = new(new MemoryCacheOptions { SizeLimit = 16 * 1024 * 1024 });
    private static readonly object Gate = new();
    private static long _generation;

    /// <summary>Loads an isolated display snapshot or stores a successful eligible read for ten seconds.</summary>
    /// <param name="key">Panel URL/root plus hashed authorization identity and exact query; never logged.</param>
    /// <param name="read">Network read invoked outside the cache lock and all database transactions.</param>
    /// <returns>Private JSON to deserialize afresh; errors are not cached.</returns>
    /// <remarks>A generation change during the read prevents stale publication. Concurrent misses may issue reads.</remarks>
    public static async Task<string> ReadAsync(string key, Func<Task<string>> read)
    {
        long generation;
        lock (Gate)
        {
            generation = _generation;
            if (Cache.TryGetValue(key, out string cached)) return cached;
        }
        var raw = await read();
        if (Newtonsoft.Json.Linq.JObject.Parse(raw).Value<bool?>("success") != true) return raw;
        lock (Gate)
            if (generation == _generation && raw.Length <= 2 * 1024 * 1024)
                Cache.Set(key, raw, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(10), Size = raw.Length * 2L });
        return raw;
    }

    /// <summary>Invalidates display snapshots when any panel mutation starts or completes, including ambiguous failures.</summary>
    /// <remarks>Global invalidation is conservative: panels never reuse stale renewal/create/delete data.</remarks>
    public static void Invalidate()
    {
        lock (Gate) { ++_generation; Cache.Clear(); }
    }
}
