using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Simulators;

public sealed class SimulatorCardReader : ICardReader
{
    private static readonly IReadOnlySet<DeviceCapability> Capabilities =
        new HashSet<DeviceCapability> { DeviceCapability.ReadCard, DeviceCapability.Status };

    private readonly object _sync = new();
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private Channel<CardReadEvent>? _cardChannel;
    private SimulatorConnectionFailure? _nextConnectionFailure;
    private int _connectionState = (int)DeviceConnectionState.Disconnected;
    private int _offline;
    private int _disposed;

    public SimulatorCardReader(Guid id, string name, DeviceEndpoint endpoint, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!string.Equals(endpoint.ConnectionType, "Simulator", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Simulator kart okuyucu endpoint türü Simulator olmalıdır.", nameof(endpoint));
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
                throw new DeviceConnectionException(Name, "Simulator kart okuyucu offline.", "SIMULATOR_OFFLINE");
            }

            SimulatorConnectionFailure? failure;
            lock (_sync)
            {
                failure = _nextConnectionFailure;
                _nextConnectionFailure = null;
                if (failure is null)
                {
                    _cardChannel = CreateChannel();
                }
            }

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
            CompleteCurrentStream();
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
            state == DeviceConnectionState.Connected ? "Simulator kart okuyucu bağlı." : "Simulator kart okuyucu bağlı değil."));
    }

    public async IAsyncEnumerable<CardReadEvent> ReadCardsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ChannelReader<CardReadEvent> reader;
        lock (_sync)
        {
            if (ConnectionState != DeviceConnectionState.Connected || _cardChannel is null)
            {
                throw DisconnectedException();
            }

            reader = _cardChannel.Reader;
        }

        await foreach (var card in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return card;
        }
    }

    public void ScanCard(string cardNumber) => WriteCards(cardNumber, 1);

    public void ScanUnknownCard(string payload) => WriteCards(payload, 1);

    public void ScanCardTwice(string cardNumber) => WriteCards(cardNumber, 2);

    public void FailNextConnection(string message = "Simulator bağlantı hatası.",
        string errorCode = "SIMULATOR_CONNECT_FAILED")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ThrowIfDisposed();
        lock (_sync)
        {
            _nextConnectionFailure = new SimulatorConnectionFailure(message, errorCode);
        }
    }

    public void GoOffline()
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _offline, 1);
        RemoteDisconnect("Simulator kart okuyucu offline oldu.", "SIMULATOR_OFFLINE");
    }

    public void GoOnline()
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _offline, 0);
    }

    public void RemoteDisconnect(string message = "Simulator uzak bağlantısı kapandı.",
        string errorCode = "SIMULATOR_REMOTE_DISCONNECT")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ThrowIfDisposed();
        var exception = new DeviceConnectionException(Name, message, errorCode);
        lock (_sync)
        {
            _cardChannel?.Writer.TryComplete(exception);
            _cardChannel = null;
        }

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
            CompleteCurrentStream();
            SetState(DeviceConnectionState.Disconnected);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void WriteCards(string payload, int count)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);
        ThrowIfDisposed();
        lock (_sync)
        {
            if (ConnectionState != DeviceConnectionState.Connected || _cardChannel is null)
            {
                throw DisconnectedException();
            }

            for (var index = 0; index < count; index++)
            {
                if (!_cardChannel.Writer.TryWrite(new CardReadEvent(payload, _timeProvider.GetUtcNow(), Name)))
                {
                    throw DisconnectedException();
                }
            }
        }
    }

    private void CompleteCurrentStream()
    {
        lock (_sync)
        {
            _cardChannel?.Writer.TryComplete();
            _cardChannel = null;
        }
    }

    private static Channel<CardReadEvent> CreateChannel() =>
        Channel.CreateUnbounded<CardReadEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private static DeviceInfo CreateInfo() => new("Simulator Card Reader", "SIM-CARD", "1.0", Capabilities);
    private DeviceConnectionException DisconnectedException() =>
        new(Name, "Simulator kart okuyucu bağlı değil.", "SIMULATOR_DISCONNECTED");
    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private void SetState(DeviceConnectionState state) => Volatile.Write(ref _connectionState, (int)state);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);

    private sealed record SimulatorConnectionFailure(string Message, string ErrorCode);
}
