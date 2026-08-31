using System.Collections.Concurrent;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Turnstiles;

namespace Yemekhane.Devices.Management;

public sealed class DeviceManager : IAsyncDisposable, IObservable<DeviceStateChange>
{
    private static readonly TimeSpan[] ReconnectDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30)
    ];

    private readonly ConcurrentDictionary<Guid, ManagedDevice> _devices = new();
    private readonly ConcurrentDictionary<IObserver<DeviceStateChange>, byte> _observers = new();
    private readonly DeviceRegistry? _deviceRegistry;
    private readonly TurnstileRegistry? _turnstileRegistry;
    private readonly IDeviceDelay _delay;
    private readonly TimeSpan _healthCheckInterval;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeProvider _timeProvider;
    private int _started;
    private int _shutdown;
    private int _disposed;

    public DeviceManager(IDeviceDelay? delay = null, TimeSpan? healthCheckInterval = null,
        DeviceRegistry? deviceRegistry = null, TurnstileRegistry? turnstileRegistry = null,
        TimeSpan? operationTimeout = null, TimeProvider? timeProvider = null)
    {
        if (healthCheckInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(healthCheckInterval));
        }

        if (operationTimeout is { } operation && operation <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout));
        }

        _delay = delay ?? new SystemDeviceDelay();
        _healthCheckInterval = healthCheckInterval ?? TimeSpan.FromSeconds(30);
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(15);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deviceRegistry = deviceRegistry;
        _turnstileRegistry = turnstileRegistry;
    }

    public event EventHandler<DeviceStateChangedEventArgs>? StateChanged;

    public IReadOnlyCollection<IDevice> Devices => _devices.Values.Select(entry => entry.Device).ToArray();

    public bool Register(IDevice device, DeviceRegistrationOptions? options = null)
    {
        ThrowIfUnavailable();
        ArgumentNullException.ThrowIfNull(device);
        options ??= new DeviceRegistrationOptions();

        var managed = new ManagedDevice(device, options);
        if (!_devices.TryAdd(device.Id, managed))
        {
            return false;
        }

        _deviceRegistry?.Register(device);
        if (device is ITurnstile turnstile)
        {
            _turnstileRegistry?.Register(turnstile);
        }

        if (Volatile.Read(ref _started) != 0 && options is { IsActive: true, AutoConnect: true })
        {
            managed.DesiredConnected = true;
            StartWorker(managed);
        }

        return true;
    }

    public bool TryGetDevice(Guid deviceId, out IDevice? device)
    {
        if (_devices.TryGetValue(deviceId, out var managed))
        {
            device = managed.Device;
            return true;
        }

        device = null;
        return false;
    }

    public async Task<bool> UnregisterAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_devices.TryRemove(deviceId, out var managed))
        {
            return false;
        }

        managed.DesiredConnected = false;
        await StopWorkerAsync(managed).ConfigureAwait(false);
        await DisconnectCoreAsync(managed, cancellationToken).ConfigureAwait(false);
        _deviceRegistry?.Unregister(deviceId);
        if (managed.Device is ITurnstile)
        {
            _turnstileRegistry?.Unregister(deviceId);
        }

        await DisposeDeviceAsync(managed, cancellationToken).ConfigureAwait(false);
        managed.Dispose();
        return true;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var starts = new List<Task>();
        foreach (var managed in _devices.Values)
        {
            if (!managed.Options.IsActive || !managed.Options.AutoConnect)
            {
                continue;
            }

            managed.DesiredConnected = true;
            StartWorker(managed);
            starts.Add(managed.FirstAttempt.Task);
        }

        await Task.WhenAll(starts).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceInfo> ConnectAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        var managed = GetManaged(deviceId);
        managed.DesiredConnected = true;
        await StopWorkerAsync(managed).ConfigureAwait(false);

        try
        {
            return await ConnectCoreAsync(managed, false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (managed.Options.IsActive && managed.DesiredConnected && Volatile.Read(ref _started) != 0)
            {
                StartWorker(managed);
            }
        }
    }

    public async Task DisconnectAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        var managed = GetManaged(deviceId);
        managed.DesiredConnected = false;
        await StopWorkerAsync(managed).ConfigureAwait(false);
        await DisconnectCoreAsync(managed, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DeviceInfo> ReconnectAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        var managed = GetManaged(deviceId);
        managed.DesiredConnected = false;
        await StopWorkerAsync(managed).ConfigureAwait(false);
        await DisconnectCoreAsync(managed, cancellationToken).ConfigureAwait(false);
        managed.DesiredConnected = true;

        try
        {
            return await ConnectCoreAsync(managed, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (managed.Options.IsActive && managed.DesiredConnected && Volatile.Read(ref _started) != 0)
            {
                StartWorker(managed);
            }
        }
    }

    public Task<DeviceStatus> TestAsync(Guid deviceId, CancellationToken cancellationToken = default) =>
        GetStatusAsync(deviceId, cancellationToken);

    public async Task<DeviceStatus> GetStatusAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        ThrowIfUnavailable();
        var managed = GetManaged(deviceId);
        await managed.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await ExecuteDeviceOperationAsync(managed,
                token => managed.Device.GetStatusAsync(token), "durum sorgusu", cancellationToken).ConfigureAwait(false);
            Publish(managed, status.State, status);
            return status;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Publish(managed, DeviceConnectionState.Faulted, exception: exception);
            throw;
        }
        finally
        {
            managed.OperationGate.Release();
        }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return;
        }
        ThrowIfDisposed();

        var devices = _devices.Values.ToArray();
        foreach (var managed in devices)
        {
            managed.DesiredConnected = false;
            managed.CancelWorker();
        }

        await Task.WhenAll(devices.Select(WaitForWorkerAsync)).ConfigureAwait(false);
        await Task.WhenAll(devices.Select(device => DisconnectIgnoringFailureAsync(device, cancellationToken)))
            .ConfigureAwait(false);
    }

    public IDisposable Subscribe(IObserver<DeviceStateChange> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ThrowIfDisposed();
        _observers.TryAdd(observer, 0);
        return new Subscription(_observers, observer);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _shutdown, 1) == 0)
        {
            var devices = _devices.Values.ToArray();
            foreach (var managed in devices)
            {
                managed.DesiredConnected = false;
                managed.CancelWorker();
            }

            await Task.WhenAll(devices.Select(WaitForWorkerAsync)).ConfigureAwait(false);
            await Task.WhenAll(devices.Select(device => DisconnectIgnoringFailureAsync(device, CancellationToken.None)))
                .ConfigureAwait(false);
        }

        foreach (var pair in _devices.ToArray())
        {
            if (!_devices.TryRemove(pair.Key, out var managed))
            {
                continue;
            }

            _deviceRegistry?.Unregister(pair.Key);
            if (managed.Device is ITurnstile)
            {
                _turnstileRegistry?.Unregister(pair.Key);
            }

            try
            {
                await DisposeDeviceAsync(managed, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // One faulty device must not prevent the remaining devices from being released.
            }

            managed.Dispose();
        }

        foreach (var observer in _observers.Keys)
        {
            try
            {
                observer.OnCompleted();
            }
            catch
            {
                // Observers are isolated from manager shutdown.
            }
        }

        _observers.Clear();
    }

    private void StartWorker(ManagedDevice managed)
    {
        lock (managed.WorkerSync)
        {
            if (managed.Worker is { IsCompleted: false })
            {
                return;
            }

            managed.ResetWorkerCancellation();
            managed.Worker = RunDeviceAsync(managed, managed.WorkerCancellation.Token);
        }
    }

    private async Task RunDeviceAsync(ManagedDevice managed, CancellationToken cancellationToken)
    {
        var retryIndex = 0;
        try
        {
            while (managed.DesiredConnected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (managed.Device.ConnectionState != DeviceConnectionState.Connected)
                {
                    try
                    {
                        await ConnectCoreAsync(managed, retryIndex > 0, cancellationToken).ConfigureAwait(false);
                        retryIndex = 0;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        managed.FirstAttempt.TrySetResult();
                        break;
                    }
                    catch
                    {
                        managed.FirstAttempt.TrySetResult();
                        var reconnectDelay = ReconnectDelays[Math.Min(retryIndex, ReconnectDelays.Length - 1)];
                        retryIndex++;
                        var lastAttemptAt = _timeProvider.GetUtcNow();
                        var nextRetryAt = lastAttemptAt + reconnectDelay;
                        Publish(managed, DeviceConnectionState.Reconnecting,
                            new DeviceStatus(DeviceConnectionState.Reconnecting, lastAttemptAt,
                                $"Son bağlantı denemesi başarısız. Sonraki deneme: {nextRetryAt:dd.MM.yyyy HH:mm:ss}."),
                            lastAttemptAt: lastAttemptAt, nextRetryAt: nextRetryAt);
                        await _delay.DelayAsync(reconnectDelay, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    managed.FirstAttempt.TrySetResult();
                }

                await _delay.DelayAsync(_healthCheckInterval, cancellationToken).ConfigureAwait(false);
                var healthy = await CheckHealthAsync(managed, cancellationToken).ConfigureAwait(false);
                if (!healthy)
                {
                    await DisconnectIgnoringFailureAsync(managed, cancellationToken).ConfigureAwait(false);
                    await _delay.DelayAsync(ReconnectDelays[0], cancellationToken).ConfigureAwait(false);
                    retryIndex = 1;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            managed.FirstAttempt.TrySetResult();
        }
        catch (Exception exception)
        {
            managed.FirstAttempt.TrySetResult();
            Publish(managed, DeviceConnectionState.Faulted, exception: exception);
        }
    }

    private async Task<DeviceInfo> ConnectCoreAsync(ManagedDevice managed, bool reconnecting,
        CancellationToken cancellationToken)
    {
        await managed.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish(managed, reconnecting ? DeviceConnectionState.Reconnecting : DeviceConnectionState.Connecting);
            var info = await ExecuteDeviceOperationAsync(managed,
                token => managed.Device.ConnectAsync(token), "bağlantı", cancellationToken).ConfigureAwait(false);
            Publish(managed, DeviceConnectionState.Connected,
                new DeviceStatus(DeviceConnectionState.Connected, _timeProvider.GetUtcNow()), info: info);
            return info;
        }
        catch (OperationCanceledException)
        {
            Publish(managed, DeviceConnectionState.Disconnected);
            throw;
        }
        catch (Exception exception)
        {
            Publish(managed, DeviceConnectionState.Faulted, exception: exception);
            throw;
        }
        finally
        {
            managed.OperationGate.Release();
        }
    }

    private async Task DisconnectCoreAsync(ManagedDevice managed, CancellationToken cancellationToken)
    {
        await managed.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteDeviceOperationAsync(managed,
                token => managed.Device.DisconnectAsync(token), "bağlantıyı kapatma", cancellationToken).ConfigureAwait(false);
            Publish(managed, DeviceConnectionState.Disconnected,
                new DeviceStatus(DeviceConnectionState.Disconnected, _timeProvider.GetUtcNow()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Publish(managed, DeviceConnectionState.Faulted, exception: exception);
            throw;
        }
        finally
        {
            managed.OperationGate.Release();
        }
    }

    private async Task<bool> CheckHealthAsync(ManagedDevice managed, CancellationToken cancellationToken)
    {
        await managed.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await ExecuteDeviceOperationAsync(managed,
                token => managed.Device.GetStatusAsync(token), "sağlık kontrolü", cancellationToken).ConfigureAwait(false);
            Publish(managed, status.State, status);
            return status.State == DeviceConnectionState.Connected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Publish(managed, DeviceConnectionState.Faulted, exception: exception);
            return false;
        }
        finally
        {
            managed.OperationGate.Release();
        }
    }

    private async Task DisconnectIgnoringFailureAsync(ManagedDevice managed, CancellationToken cancellationToken)
    {
        try
        {
            await DisconnectCoreAsync(managed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Disconnect and dispose of other devices must continue independently.
        }
    }

    private static async Task WaitForWorkerAsync(ManagedDevice managed)
    {
        Task? worker;
        lock (managed.WorkerSync)
        {
            worker = managed.Worker;
        }

        if (worker is not null)
        {
            await worker.ConfigureAwait(false);
        }
    }

    private static async Task StopWorkerAsync(ManagedDevice managed)
    {
        managed.CancelWorker();
        await WaitForWorkerAsync(managed).ConfigureAwait(false);
    }

    private void Publish(ManagedDevice managed, DeviceConnectionState state, DeviceStatus? status = null,
        Exception? exception = null, DeviceInfo? info = null, DateTimeOffset? lastAttemptAt = null,
        DateTimeOffset? nextRetryAt = null)
    {
        var previous = (DeviceConnectionState)Interlocked.Exchange(ref managed.LastPublishedState, (int)state);
        var change = new DeviceStateChange(managed.Device.Id, managed.Device.Name, previous, state,
            _timeProvider.GetUtcNow(), status, exception, info, lastAttemptAt, nextRetryAt);

        var handlers = StateChanged;
        if (handlers is not null)
        {
            var arguments = new DeviceStateChangedEventArgs(change);
            foreach (EventHandler<DeviceStateChangedEventArgs> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(this, arguments);
                }
                catch
                {
                    // Subscribers cannot disrupt a device lifecycle.
                }
            }
        }

        foreach (var observer in _observers.Keys)
        {
            try
            {
                observer.OnNext(change);
            }
            catch
            {
                // Observers are isolated from one another and from device workers.
            }
        }
    }

    private ManagedDevice GetManaged(Guid deviceId) =>
        _devices.TryGetValue(deviceId, out var managed)
            ? managed
            : throw new KeyNotFoundException($"Cihaz kayıtlı değil: {deviceId}");

    private async Task<T> ExecuteDeviceOperationAsync<T>(ManagedDevice managed,
        Func<CancellationToken, Task<T>> operation, string operationName, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        try
        {
            return await operation(timeout.Token).WaitAsync(_operationTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            throw new DeviceConnectionException(managed.Device.Name,
                $"{operationName} {_operationTimeout.TotalSeconds:0.##} saniyede tamamlanmadı.",
                "DEVICE_OPERATION_TIMEOUT", exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DeviceConnectionException(managed.Device.Name,
                $"{operationName} {_operationTimeout.TotalSeconds:0.##} saniyede tamamlanmadı.",
                "DEVICE_OPERATION_TIMEOUT", exception);
        }
    }

    private Task<bool> ExecuteDeviceOperationAsync(ManagedDevice managed,
        Func<CancellationToken, Task> operation, string operationName, CancellationToken cancellationToken) =>
        ExecuteDeviceOperationAsync(managed, async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, operationName, cancellationToken);

    private async Task DisposeDeviceAsync(ManagedDevice managed, CancellationToken cancellationToken)
    {
        try
        {
            await managed.Device.DisposeAsync().AsTask().WaitAsync(_operationTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new DeviceConnectionException(managed.Device.Name,
                $"kaynak kapatma {_operationTimeout.TotalSeconds:0.##} saniyede tamamlanmadı.",
                "DEVICE_DISPOSE_TIMEOUT", exception);
        }
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _shutdown) != 0, this);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class ManagedDevice(IDevice device, DeviceRegistrationOptions options) : IDisposable
    {
        public IDevice Device { get; } = device;
        public DeviceRegistrationOptions Options { get; } = options;
        public SemaphoreSlim OperationGate { get; } = new(1, 1);
        public object WorkerSync { get; } = new();
        public TaskCompletionSource FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource WorkerCancellation { get; private set; } = new();
        public Task? Worker { get; set; }
        public bool DesiredConnected { get; set; }
        public int LastPublishedState = (int)device.ConnectionState;

        public void CancelWorker()
        {
            lock (WorkerSync)
            {
                WorkerCancellation.Cancel();
            }
        }

        public void ResetWorkerCancellation()
        {
            WorkerCancellation.Dispose();
            WorkerCancellation = new CancellationTokenSource();
        }

        public void Dispose()
        {
            WorkerCancellation.Dispose();
            OperationGate.Dispose();
        }
    }

    private sealed class Subscription(
        ConcurrentDictionary<IObserver<DeviceStateChange>, byte> observers,
        IObserver<DeviceStateChange> observer) : IDisposable
    {
        public void Dispose() => observers.TryRemove(observer, out _);
    }
}
