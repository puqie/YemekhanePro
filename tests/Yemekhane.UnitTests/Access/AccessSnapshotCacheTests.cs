using Yemekhane.Application.Access;
using Yemekhane.Infrastructure.Access;

namespace Yemekhane.UnitTests.Access;

public sealed class AccessSnapshotCacheTests
{
    private static readonly Guid StudentId = Guid.NewGuid();
    private static readonly Guid DeviceId = Guid.NewGuid();
    private static readonly Guid MealId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 8, 31);

    [Fact]
    public void RecordsHitMissAndExpiresAtTtl()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 8, 31, 10, 0, 0, TimeSpan.Zero));
        using var metrics = new AccessPerformanceMetrics();
        var cache = new AccessSnapshotCache(clock, metrics, new AccessCacheOptions { SizeLimit = 2, Ttl = TimeSpan.FromSeconds(10) });

        Assert.False(cache.TryGet("1", DeviceId, MealId, Today, out _));
        cache.Set("1", DeviceId, MealId, Today, Snapshot(StudentId));
        Assert.True(cache.TryGet("1", DeviceId, MealId, Today, out _));
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.False(cache.TryGet("1", DeviceId, MealId, Today, out _));

        Assert.Equal(1, metrics.CacheHits);
        Assert.Equal(2, metrics.CacheMisses);
    }

    [Fact]
    public void DateIsPartOfKeyAndStudentInvalidationRemovesAllVariants()
    {
        using var metrics = new AccessPerformanceMetrics();
        var cache = new AccessSnapshotCache(TimeProvider.System, metrics);
        cache.Set("1", DeviceId, MealId, Today, Snapshot(StudentId));
        cache.Set("1", DeviceId, MealId, Today.AddDays(1), Snapshot(StudentId));

        cache.Publish(new(StudentId: StudentId));

        Assert.False(cache.TryGet("1", DeviceId, MealId, Today, out _));
        Assert.False(cache.TryGet("1", DeviceId, MealId, Today.AddDays(1), out _));
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, metrics.CacheInvalidations);
    }

    [Fact]
    public void LeastRecentlyUsedEntriesKeepMemoryBounded()
    {
        using var metrics = new AccessPerformanceMetrics();
        var cache = new AccessSnapshotCache(TimeProvider.System, metrics,
            new AccessCacheOptions { SizeLimit = 2, Ttl = TimeSpan.FromMinutes(1) });

        cache.Set("1", DeviceId, MealId, Today, Snapshot(Guid.NewGuid()));
        cache.Set("2", DeviceId, MealId, Today, Snapshot(Guid.NewGuid()));
        Assert.True(cache.TryGet("1", DeviceId, MealId, Today, out _));
        cache.Set("3", DeviceId, MealId, Today, Snapshot(Guid.NewGuid()));

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("1", DeviceId, MealId, Today, out _));
        Assert.False(cache.TryGet("2", DeviceId, MealId, Today, out _));
        Assert.True(cache.TryGet("3", DeviceId, MealId, Today, out _));
    }

    private static AccessSnapshot Snapshot(Guid studentId) =>
        new(true, true, studentId, "Test Student", null, true, true, Guid.NewGuid(), 1, 0, "Active", false);

    private sealed class TestTimeProvider(DateTimeOffset value) : TimeProvider
    {
        private DateTimeOffset now = value;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
