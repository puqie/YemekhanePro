using System.Diagnostics.Metrics;
using Yemekhane.Application.Access;

namespace Yemekhane.Infrastructure.Access;

public sealed class AccessCacheOptions
{
    public const int DefaultSizeLimit = 4_096;
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(20);

    public int SizeLimit { get; init; } = DefaultSizeLimit;
    public TimeSpan Ttl { get; init; } = DefaultTtl;
}

public sealed class AccessPerformanceMetrics : IDisposable
{
    private readonly Meter meter = new("Yemekhane.Access", "1.0.0");
    private readonly Counter<long> cacheHits;
    private readonly Counter<long> cacheMisses;
    private readonly Counter<long> cacheInvalidations;
    private readonly Histogram<double> lookupDuration;
    private long hitCount;
    private long missCount;
    private long invalidationCount;

    public AccessPerformanceMetrics()
    {
        cacheHits = meter.CreateCounter<long>("access.cache.hits");
        cacheMisses = meter.CreateCounter<long>("access.cache.misses");
        cacheInvalidations = meter.CreateCounter<long>("access.cache.invalidations");
        lookupDuration = meter.CreateHistogram<double>("access.snapshot.duration", "ms");
    }

    public long CacheHits => Interlocked.Read(ref hitCount);
    public long CacheMisses => Interlocked.Read(ref missCount);
    public long CacheInvalidations => Interlocked.Read(ref invalidationCount);

    internal void Hit() { Interlocked.Increment(ref hitCount); cacheHits.Add(1); }
    internal void Miss() { Interlocked.Increment(ref missCount); cacheMisses.Add(1); }
    internal void Invalidated(long count) { Interlocked.Add(ref invalidationCount, count); cacheInvalidations.Add(count); }
    internal void RecordLookup(TimeSpan elapsed, bool cacheHit) =>
        lookupDuration.Record(elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("cache.hit", cacheHit));

    public void Dispose() => meter.Dispose();
}

public sealed class AccessSnapshotCache : IAccessCacheInvalidationSink
{
    private readonly record struct CacheKey(string CardNumber, Guid DeviceId, Guid MealTypeId, DateOnly Date);
    private sealed record CacheEntry(AccessSnapshot Snapshot, DateTimeOffset ExpiresAt, LinkedListNode<CacheKey> Node);

    private readonly object gate = new();
    private readonly Dictionary<CacheKey, CacheEntry> entries = [];
    private readonly LinkedList<CacheKey> lru = [];
    private readonly TimeProvider timeProvider;
    private readonly AccessCacheOptions options;
    private readonly AccessPerformanceMetrics metrics;

    public AccessSnapshotCache(TimeProvider timeProvider, AccessPerformanceMetrics metrics, AccessCacheOptions? options = null)
    {
        this.timeProvider = timeProvider;
        this.metrics = metrics;
        this.options = options ?? new AccessCacheOptions();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(this.options.SizeLimit);
        if (this.options.Ttl <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
    }

    public int Count { get { lock (gate) return entries.Count; } }

    public bool TryGet(string cardNumber, Guid deviceId, Guid mealTypeId, DateOnly date, out AccessSnapshot snapshot)
    {
        var key = new CacheKey(cardNumber, deviceId, mealTypeId, date);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var entry))
            {
                if (entry.ExpiresAt > timeProvider.GetUtcNow())
                {
                    lru.Remove(entry.Node);
                    lru.AddLast(entry.Node);
                    snapshot = entry.Snapshot;
                    metrics.Hit();
                    return true;
                }
                Remove(key, entry);
            }
        }
        snapshot = default!;
        metrics.Miss();
        return false;
    }

    public void Set(string cardNumber, Guid deviceId, Guid mealTypeId, DateOnly date, AccessSnapshot snapshot)
    {
        var key = new CacheKey(cardNumber, deviceId, mealTypeId, date);
        lock (gate)
        {
            if (entries.Remove(key, out var existing)) lru.Remove(existing.Node);
            var node = lru.AddLast(key);
            entries[key] = new CacheEntry(snapshot, timeProvider.GetUtcNow() + options.Ttl, node);
            while (entries.Count > options.SizeLimit && lru.First is { } oldest)
            {
                entries.Remove(oldest.Value);
                lru.RemoveFirst();
            }
        }
    }

    public void Publish(AccessCacheInvalidation invalidation)
    {
        lock (gate)
        {
            if (invalidation.ClearAll)
            {
                entries.Clear();
                lru.Clear();
            }
            else
            {
                var keys = entries.Where(x =>
                    (invalidation.CardNumber is not null && x.Key.CardNumber == invalidation.CardNumber) ||
                    (invalidation.StudentId.HasValue && x.Value.Snapshot.StudentId == invalidation.StudentId))
                    .Select(x => x.Key).ToArray();
                foreach (var key in keys) Remove(key, entries[key]);
            }
        }
        metrics.Invalidated(1);
    }

    private void Remove(CacheKey key, CacheEntry entry)
    {
        entries.Remove(key);
        lru.Remove(entry.Node);
    }
}
