using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Devices;

public sealed class DeviceManagerTests
{
    [Fact]
    public async Task StartConnectsActiveAutoConnectDevicesInParallelAndIsolatesFailures()
    {
        var delay = new FakeDelay();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeDevice("first", async token =>
        {
            firstEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new InvalidOperationException("first failed");
        });
        var second = new FakeDevice("second", async token =>
        {
            secondEntered.TrySetResult();
            await release.Task.WaitAsync(token);
            return FakeDevice.Info;
        });
        var inactive = new FakeDevice("inactive");
        await using var manager = new DeviceManager(delay, TimeSpan.FromMinutes(1));
        manager.Register(first);
        manager.Register(second);
        manager.Register(inactive, new DeviceRegistrationOptions(IsActive: false));

        var start = manager.StartAsync();
        await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TimeSpan.FromSeconds(2));
        release.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, first.ConnectCount);
        Assert.Equal(1, second.ConnectCount);
        Assert.Equal(0, inactive.ConnectCount);
        Assert.Equal(DeviceConnectionState.Connected, second.ConnectionState);
    }

    [Fact]
    public async Task FailedConnectionsUseExactExponentialBackoffSequence()
    {
        var delay = new FakeDelay();
        var failures = 6;
        var device = new FakeDevice("retry", _ =>
        {
            if (Interlocked.Decrement(ref failures) >= 0)
            {
                throw new InvalidOperationException("offline");
            }

            return Task.FromResult(FakeDevice.Info);
        });
        await using var manager = new DeviceManager(delay, TimeSpan.FromMinutes(1));
        manager.Register(device);

        await manager.StartAsync();
        TimeSpan[] expected = [
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), TimeSpan.FromSeconds(30)
        ];
        foreach (var value in expected)
        {
            var pending = await delay.NextAsync();
            Assert.Equal(value, pending.Duration);
            pending.Complete();
        }

        await WaitUntilAsync(() => device.ConnectionState == DeviceConnectionState.Connected);
        Assert.Equal(expected, delay.Durations.Take(expected.Length));
    }

    [Fact]
    public async Task UnregisterCancelsOnlyTargetDeviceLifecycleAndDisposesIt()
    {
        var delay = new FakeDelay();
        var blocked = new FakeDevice("blocked", async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return FakeDevice.Info;
        });
        var healthy = new FakeDevice("healthy");
        await using var manager = new DeviceManager(delay);
        manager.Register(blocked);
        manager.Register(healthy);
        var start = manager.StartAsync();
        await WaitUntilAsync(() => blocked.ConnectCount == 1 && healthy.ConnectCount == 1);

        Assert.True(await manager.UnregisterAsync(blocked.Id));
        await start.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(blocked.ConnectWasCancelled);
        Assert.True(blocked.IsDisposed);
        Assert.Equal(DeviceConnectionState.Connected, healthy.ConnectionState);
    }

    [Fact]
    public async Task ShutdownCancelsWorkersAndDisconnectsEveryDevice()
    {
        var delay = new FakeDelay();
        var first = new FakeDevice("first");
        var second = new FakeDevice("second");
        await using var manager = new DeviceManager(delay);
        manager.Register(first);
        manager.Register(second);
        await manager.StartAsync();

        await manager.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(DeviceConnectionState.Disconnected, first.ConnectionState);
        Assert.Equal(DeviceConnectionState.Disconnected, second.ConnectionState);
        Assert.Equal(1, first.DisconnectCount);
        Assert.Equal(1, second.DisconnectCount);
        Assert.All(delay.Pending, pending => Assert.True(pending.CancellationToken.IsCancellationRequested));
    }

    [Fact]
    public async Task HealthCheckReconnectsAfterDisconnectUsingBackoff()
    {
        var delay = new FakeDelay();
        var device = new FakeDevice("recovering");
        device.Statuses.Enqueue(DeviceConnectionState.Disconnected);
        await using var manager = new DeviceManager(delay, TimeSpan.FromSeconds(15));
        manager.Register(device);
        await manager.StartAsync();

        var healthDelay = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), healthDelay.Duration);
        healthDelay.Complete();
        var reconnectDelay = await delay.NextAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), reconnectDelay.Duration);
        reconnectDelay.Complete();

        await WaitUntilAsync(() => device.ConnectCount == 2);
        Assert.Equal(DeviceConnectionState.Connected, device.ConnectionState);
    }

    [Fact]
    public async Task ManualOperationsPublishStateAndKeepRegistriesCompatible()
    {
        var registry = new DeviceRegistry();
        var device = new FakeDevice("manual");
        await using var manager = new DeviceManager(deviceRegistry: registry);
        var changes = new ConcurrentQueue<DeviceStateChange>();
        manager.StateChanged += (_, args) => changes.Enqueue(args.Change);
        manager.Register(device, new DeviceRegistrationOptions(AutoConnect: false));

        await manager.ConnectAsync(device.Id);
        var status = await manager.TestAsync(device.Id);
        await manager.DisconnectAsync(device.Id);

        Assert.True(registry.TryResolve(device.Id, out var resolved));
        Assert.Same(device, resolved);
        Assert.Equal(DeviceConnectionState.Connected, status.State);
        Assert.Contains(changes, change => change.State == DeviceConnectionState.Connected);
        Assert.Contains(changes, change => change.State == DeviceConnectionState.Disconnected);
    }

    [Fact]
    public async Task HungDeviceDoesNotDelayHealthyDeviceAndShutdownIsBounded()
    {
        var delay = new FakeDelay();
        var hung = new FakeDevice("hung", _ =>
            new TaskCompletionSource<DeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously).Task,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        var healthy = new FakeDevice("healthy");
        await using var manager = new DeviceManager(delay, TimeSpan.FromMinutes(1),
            operationTimeout: TimeSpan.FromMilliseconds(50));
        manager.Register(hung);
        manager.Register(healthy);

        // Asili cihaz saglikli cihazi engellemez: engelleseydi StartAsync hic tamamlanmaz ve
        // asagidaki WaitAsync zaman asimina ugrardi. Ek bir duvar saati olcumu ayni garantiyi
        // tekrar etmekten baska bir sey saglamaz, yalnizca yuk altinda testi kararsiz yapar.
        await manager.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DeviceConnectionState.Connected, healthy.ConnectionState);
        Assert.NotEqual(DeviceConnectionState.Connected, hung.ConnectionState);
        await manager.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RetryEventContainsExactLastAttemptAndNextRetryTimes()
    {
        var now = new DateTimeOffset(2026, 8, 31, 17, 0, 0, TimeSpan.Zero);
        var delay = new FakeDelay();
        var device = new FakeDevice("retry", _ => throw new IOException("offline"));
        await using var manager = new DeviceManager(delay, TimeSpan.FromMinutes(1),
            operationTimeout: TimeSpan.FromSeconds(1), timeProvider: new FixedTimeProvider(now));
        var changes = new ConcurrentQueue<DeviceStateChange>();
        manager.StateChanged += (_, args) => changes.Enqueue(args.Change);
        manager.Register(device);

        await manager.StartAsync();
        var pending = await delay.NextAsync();
        var retry = Assert.Single(changes, change => change.State == DeviceConnectionState.Reconnecting);

        Assert.Equal(now, retry.LastAttemptAt);
        Assert.Equal(now.AddSeconds(1), retry.NextRetryAt);
        Assert.Contains("31.08.2026 17:00:01", retry.Status!.Message, StringComparison.Ordinal);
        pending.Complete();
    }

    [Fact]
    public async Task HungHardwareWorkerDoesNotBlockApiStudentReportOrCashDatabaseQueries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
        await using var db = new YemekhaneDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Students.Add(new Student { StudentNo = "057", FirstName = "Donanım", LastName = "Test" });
        db.Set<IncomeType>().Add(new IncomeType { Name = "Nakit" });
        await db.SaveChangesAsync();

        var delay = new FakeDelay();
        var hung = new FakeDevice("hung", _ =>
            new TaskCompletionSource<DeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously).Task);
        await using var manager = new DeviceManager(delay, operationTimeout: TimeSpan.FromMilliseconds(50));
        manager.Register(hung);
        var hardwareStart = manager.StartAsync();
        var startedAt = DateTimeOffset.UtcNow;

        Assert.Equal(1, await db.Students.AsNoTracking().CountAsync());
        Assert.Equal(1, await db.Set<IncomeType>().AsNoTracking().CountAsync());
        Assert.Equal(0, await db.AccessLogs.AsNoTracking().CountAsync());
        Assert.Equal(0, await db.Devices.AsNoTracking().CountAsync());
        Assert.True(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(1));
        await hardwareStart.WaitAsync(TimeSpan.FromSeconds(1));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeDelay : IDeviceDelay, IDisposable
    {
        private readonly ConcurrentQueue<PendingDelay> _newDelays = new();
        private readonly SemaphoreSlim _available = new(0);

        public ConcurrentQueue<PendingDelay> Pending { get; } = new();
        public IReadOnlyList<TimeSpan> Durations => Pending.Select(item => item.Duration).ToArray();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var pending = new PendingDelay(delay, cancellationToken);
            Pending.Enqueue(pending);
            _newDelays.Enqueue(pending);
            _available.Release();
            return pending.Task;
        }

        public async Task<PendingDelay> NextAsync()
        {
            await _available.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(_newDelays.TryDequeue(out var pending));
            return pending;
        }

        public void Dispose() => _available.Dispose();
    }

    private sealed class PendingDelay
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public PendingDelay(TimeSpan duration, CancellationToken cancellationToken)
        {
            Duration = duration;
            CancellationToken = cancellationToken;
            _registration = cancellationToken.Register(() => _completion.TrySetCanceled(cancellationToken));
        }

        public TimeSpan Duration { get; }
        public CancellationToken CancellationToken { get; }
        public Task Task => _completion.Task;

        public void Complete()
        {
            _registration.Dispose();
            _completion.TrySetResult();
        }
    }

    private sealed class FakeDevice(string name, Func<CancellationToken, Task<DeviceInfo>>? connect = null,
        Func<CancellationToken, Task>? disconnect = null) : IDevice
    {
        private int _state = (int)DeviceConnectionState.Disconnected;

        public static DeviceInfo Info { get; } = new("Fake", null, null,
            new HashSet<DeviceCapability> { DeviceCapability.Status });
        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; } = name;
        public DeviceEndpoint Endpoint { get; } = new("Fake");
        public DeviceConnectionState ConnectionState => (DeviceConnectionState)Volatile.Read(ref _state);
        public ConcurrentQueue<DeviceConnectionState> Statuses { get; } = new();
        public int ConnectCount { get; private set; }
        public int DisconnectCount { get; private set; }
        public bool ConnectWasCancelled { get; private set; }
        public bool IsDisposed { get; private set; }

        public async Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectCount++;
            Volatile.Write(ref _state, (int)DeviceConnectionState.Connecting);
            try
            {
                var info = connect is null ? Info : await connect(cancellationToken);
                Volatile.Write(ref _state, (int)DeviceConnectionState.Connected);
                return info;
            }
            catch (OperationCanceledException)
            {
                ConnectWasCancelled = true;
                Volatile.Write(ref _state, (int)DeviceConnectionState.Disconnected);
                throw;
            }
            catch
            {
                Volatile.Write(ref _state, (int)DeviceConnectionState.Faulted);
                throw;
            }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisconnectCount++;
            if (disconnect is not null) await disconnect(cancellationToken);
            Volatile.Write(ref _state, (int)DeviceConnectionState.Disconnected);
        }

        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Statuses.TryDequeue(out var state))
            {
                Volatile.Write(ref _state, (int)state);
            }

            return Task.FromResult(new DeviceStatus(ConnectionState, DateTimeOffset.UtcNow));
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            Volatile.Write(ref _state, (int)DeviceConnectionState.Disconnected);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
