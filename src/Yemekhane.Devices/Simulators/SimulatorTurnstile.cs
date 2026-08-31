using System.Collections.Concurrent;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Simulators;

public enum SimulatorCommandBehavior
{
    Succeed,
    Fail,
    Timeout
}

public enum SimulatorTurnstileCommand
{
    Grant,
    Deny
}

public sealed record SimulatorCommandHistoryEntry(
    long Sequence,
    SimulatorTurnstileCommand Command,
    TurnstileDirection Direction,
    SimulatorCommandBehavior Behavior,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    DeviceCommandResult? Result);

public sealed class SimulatorTurnstile : ITurnstile, IDeviceCapabilityProvider
{
    private static readonly IReadOnlySet<DeviceCapability> SupportedCapabilities = new HashSet<DeviceCapability>
    {
        DeviceCapability.GrantAccess,
        DeviceCapability.DenyAccess,
        DeviceCapability.Status
    };

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentQueue<SimulatorCommandBehavior> _commandBehaviors = new();
    private readonly ConcurrentDictionary<long, SimulatorCommandHistoryEntry> _history = new();
    private readonly TimeProvider _timeProvider;
    private SimulatorConnectionFailure? _nextConnectionFailure;
    private long _commandSequence;
    private int _connectionState = (int)DeviceConnectionState.Disconnected;
    private int _offline;
    private int _disposed;

    public SimulatorTurnstile(Guid id, string name, DeviceEndpoint endpoint, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!string.Equals(endpoint.ConnectionType, "Simulator", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Simulator turnike endpoint türü Simulator olmalıdır.", nameof(endpoint));
        }

        Id = id;
        Name = name;
        Endpoint = endpoint;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Guid Id { get; }
    public string Name { get; }
    public DeviceEndpoint Endpoint { get; }
    public DeviceConnectionState ConnectionState => (DeviceConnectionState)Volatile.Read(ref _connectionState);
    public IReadOnlySet<DeviceCapability> Capabilities => SupportedCapabilities;
    public IReadOnlyList<SimulatorCommandHistoryEntry> CommandHistory =>
        _history.Values.OrderBy(entry => entry.Sequence).ToArray();

    public async Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (ConnectionState == DeviceConnectionState.Connected)
            {
                return CreateInfo();
            }

            SetState(DeviceConnectionState.Connecting);
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _offline) != 0)
            {
                SetState(DeviceConnectionState.Disconnected);
                throw new DeviceConnectionException(Name, "Simulator turnike offline.", "SIMULATOR_OFFLINE");
            }

            var failure = Interlocked.Exchange(ref _nextConnectionFailure, null);
            if (failure is not null)
            {
                SetState(DeviceConnectionState.Faulted);
                throw new DeviceConnectionException(Name, failure.Message, failure.ErrorCode);
            }

            SetState(DeviceConnectionState.Connected);
            return CreateInfo();
        }
        catch (OperationCanceledException)
        {
            SetState(DeviceConnectionState.Disconnected);
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetState(DeviceConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var state = Volatile.Read(ref _offline) != 0
            ? DeviceConnectionState.Disconnected
            : ConnectionState;
        if (state != ConnectionState)
        {
            SetState(state);
        }

        return Task.FromResult(new DeviceStatus(state, _timeProvider.GetUtcNow(),
            state == DeviceConnectionState.Connected ? "Simulator turnike bağlı." : "Simulator turnike bağlı değil."));
    }

    public Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction,
        CancellationToken cancellationToken) => ExecuteCommandAsync(SimulatorTurnstileCommand.Grant, direction, cancellationToken);

    public Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction,
        CancellationToken cancellationToken) => ExecuteCommandAsync(SimulatorTurnstileCommand.Deny, direction, cancellationToken);

    public void EnqueueCommandBehavior(SimulatorCommandBehavior behavior)
    {
        ThrowIfDisposed();
        _commandBehaviors.Enqueue(behavior);
    }

    public void FailNextConnection(string message = "Simulator bağlantı hatası.",
        string errorCode = "SIMULATOR_CONNECT_FAILED")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ThrowIfDisposed();
        Interlocked.Exchange(ref _nextConnectionFailure, new SimulatorConnectionFailure(message, errorCode));
    }

    public void GoOffline()
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _offline, 1);
        SetState(DeviceConnectionState.Disconnected);
    }

    public void GoOnline()
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _offline, 0);
    }

    public void RemoteDisconnect()
    {
        ThrowIfDisposed();
        SetState(DeviceConnectionState.Disconnected);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            SetState(DeviceConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<DeviceCommandResult> ExecuteCommandAsync(SimulatorTurnstileCommand command,
        TurnstileDirection direction, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (ConnectionState != DeviceConnectionState.Connected || Volatile.Read(ref _offline) != 0)
        {
            throw new DeviceConnectionException(Name, "Simulator turnike bağlı değil.", "SIMULATOR_DISCONNECTED");
        }

        var behavior = _commandBehaviors.TryDequeue(out var configured)
            ? configured
            : SimulatorCommandBehavior.Succeed;
        var sequence = Interlocked.Increment(ref _commandSequence);
        var startedAt = _timeProvider.GetUtcNow();

        if (behavior == SimulatorCommandBehavior.Timeout)
        {
            var pending = new SimulatorCommandHistoryEntry(sequence, command, direction, behavior,
                startedAt, null, null);
            _history[sequence] = pending;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _history[sequence] = pending with { CompletedAt = _timeProvider.GetUtcNow() };
                throw;
            }
        }

        var result = behavior == SimulatorCommandBehavior.Fail
            ? new DeviceCommandResult(false, "Simulator komut hatası.", "SIMULATOR_COMMAND_FAILED")
            : new DeviceCommandResult(true,
                command == SimulatorTurnstileCommand.Grant ? "Simulator geçişe izin verdi." : "Simulator geçişi reddetti.");
        _history[sequence] = new SimulatorCommandHistoryEntry(sequence, command, direction, behavior,
            startedAt, _timeProvider.GetUtcNow(), result);
        return result;
    }

    private static DeviceInfo CreateInfo() => new("Simulator Turnstile", "SIM-TURNSTILE", "1.0", SupportedCapabilities);
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private void SetState(DeviceConnectionState state) => Volatile.Write(ref _connectionState, (int)state);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private sealed record SimulatorConnectionFailure(string Message, string ErrorCode);
}
