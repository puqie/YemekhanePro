using System.Runtime.CompilerServices;
using System.Text;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.CardReaders;

public sealed class ComCardReader : ICardReader
{
    private const int BufferSize = 256;
    private const int MaximumCardLength = 128;
    private static readonly IReadOnlySet<DeviceCapability> Capabilities =
        new HashSet<DeviceCapability> { DeviceCapability.ReadCard, DeviceCapability.Status };

    private readonly ISerialTransport _transport;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _connectTimeout;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private int _connectionState = (int)DeviceConnectionState.Disconnected;
    private int _disposed;

    public ComCardReader(Guid id, string name, DeviceEndpoint endpoint, TimeSpan? readTimeout = null)
        : this(id, name, endpoint, CreateTransport(endpoint), readTimeout)
    {
    }

    internal ComCardReader(Guid id, string name, DeviceEndpoint endpoint, ISerialTransport transport,
        TimeSpan? readTimeout = null, TimeSpan? connectTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transport);

        if (!string.Equals(endpoint.ConnectionType, "COM", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Bağlantı türü COM olmalıdır.", nameof(endpoint));
        }

        if (readTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(readTimeout), "Okuma zaman aşımı sıfırdan büyük olmalıdır.");
        }

        if (connectTimeout is { } connect && connect <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(connectTimeout), "Bağlantı zaman aşımı sıfırdan büyük olmalıdır.");
        }

        Id = id;
        Name = name;
        Endpoint = endpoint;
        _transport = transport;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
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
            if (_transport.IsOpen)
            {
                SetConnectionState(DeviceConnectionState.Connected);
                return CreateDeviceInfo();
            }

            SetConnectionState(DeviceConnectionState.Connecting);
            try
            {
                // SerialPort.Open() senkron bloklar; iptal token'i bloklanmis thread'i serbest birakamaz.
                // WaitAsync ile bekleyisi biz birakiyoruz, aksi halde takili bir port host baslangicini kilitler.
                var open = _transport.OpenAsync(cancellationToken);
                try
                {
                    await open.WaitAsync(_connectTimeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    // Arkada kalan acilis sonradan basarili olursa handle sizmasin diye kapatilir.
                    _ = open.ContinueWith(static (_, state) => ((ISerialTransport)state!).CloseAsync(CancellationToken.None),
                        _transport, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion,
                        TaskScheduler.Default);
                    SetConnectionState(DeviceConnectionState.Faulted);
                    throw new DeviceConnectionException(Name,
                        $"{Endpoint.ComPort} seri portu {_connectTimeout.TotalSeconds:0.##} saniyede açılmadı.",
                        "COM_CONNECT_TIMEOUT");
                }

                if (!_transport.IsOpen)
                {
                    SetConnectionState(DeviceConnectionState.Faulted);
                    throw new DeviceConnectionException(Name, $"{Endpoint.ComPort} seri portu açılamadı.", "COM_OPEN_FAILED");
                }

                SetConnectionState(DeviceConnectionState.Connected);
                return CreateDeviceInfo();
            }
            catch (OperationCanceledException)
            {
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw;
            }
            catch (DeviceConnectionException)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetConnectionState(DeviceConnectionState.Faulted);
                throw new DeviceConnectionException(Name, $"{Endpoint.ComPort} seri portuna bağlanılamadı.",
                    "COM_OPEN_FAILED", exception);
            }
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
            if (IsDisposed)
            {
                return;
            }

            if (!_transport.IsOpen)
            {
                SetConnectionState(DeviceConnectionState.Disconnected);
                return;
            }

            try
            {
                await _transport.CloseAsync(cancellationToken).ConfigureAwait(false);
                SetConnectionState(DeviceConnectionState.Disconnected);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetConnectionState(DeviceConnectionState.Faulted);
                throw new DeviceConnectionException(Name, $"{Endpoint.ComPort} seri portu kapatılamadı.",
                    "COM_CLOSE_FAILED", exception);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = ConnectionState;
        if (state == DeviceConnectionState.Connected && !_transport.IsOpen)
        {
            state = DeviceConnectionState.Disconnected;
            SetConnectionState(state);
        }

        var message = state == DeviceConnectionState.Connected ? "Seri port bağlı." : "Seri port bağlı değil.";
        return Task.FromResult(new DeviceStatus(state, DateTimeOffset.UtcNow, message));
    }

    public async IAsyncEnumerable<CardReadEvent> ReadCardsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ConnectionState != DeviceConnectionState.Connected || !_transport.IsOpen)
        {
            SetConnectionState(DeviceConnectionState.Disconnected);
            throw CreateDisconnectedException();
        }

        var bytes = new byte[BufferSize];
        var line = new StringBuilder();
        var discardingInvalidLine = false;

        while (true)
        {
            var count = await ReadWithTimeoutAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw CreateDisconnectedException();
            }

            for (var index = 0; index < count; index++)
            {
                var value = bytes[index];
                if (value is (byte)'\r' or (byte)'\n')
                {
                    if (!discardingInvalidLine && TryParseCardNumber(line, out var cardNumber))
                    {
                        yield return new CardReadEvent(cardNumber, DateTimeOffset.UtcNow, Name);
                    }

                    line.Clear();
                    discardingInvalidLine = false;
                    continue;
                }

                if (discardingInvalidLine)
                {
                    continue;
                }

                if (value < 0x20 || value > 0x7e || line.Length >= MaximumCardLength)
                {
                    line.Clear();
                    discardingInvalidLine = true;
                    continue;
                }

                line.Append((char)value);
            }
        }
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
            await _transport.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Disposing must still release the serial handle after a close failure.
        }

        try
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            SetConnectionState(DeviceConnectionState.Disconnected);
            _lifecycleLock.Release();
        }
    }

    private async ValueTask<int> ReadWithTimeoutAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readTimeout);
        try
        {
            return await _transport.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            if (!_transport.IsOpen)
            {
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw CreateDisconnectedException();
            }

            throw new DeviceConnectionException(Name,
                $"{Endpoint.ComPort} seri portundan kart okuma zaman aşımına uğradı.", "COM_READ_TIMEOUT", exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!_transport.IsOpen)
            {
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw CreateDisconnectedException();
            }

            SetConnectionState(DeviceConnectionState.Faulted);
            throw new DeviceConnectionException(Name, $"{Endpoint.ComPort} seri portundan kart okunamadı.",
                "COM_READ_FAILED", exception);
        }
    }

    private static bool TryParseCardNumber(StringBuilder line, out string cardNumber)
    {
        cardNumber = line.ToString().Trim();
        if (cardNumber.Length == 0)
        {
            return false;
        }

        foreach (var character in cardNumber)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')
            {
                cardNumber = string.Empty;
                return false;
            }
        }

        return true;
    }

    private static SerialPortTransport CreateTransport(DeviceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.ComPort);
        if (endpoint.BaudRate is null or <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint), "Geçerli bir baud rate belirtilmelidir.");
        }

        return new SerialPortTransport(endpoint.ComPort, endpoint.BaudRate.Value);
    }

    private static DeviceInfo CreateDeviceInfo() => new("COM Card Reader", null, null, Capabilities);

    private DeviceConnectionException CreateDisconnectedException() => new(Name,
        $"{Endpoint.ComPort} seri portu bağlı değil.", "COM_DISCONNECTED");

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void SetConnectionState(DeviceConnectionState state) =>
        Volatile.Write(ref _connectionState, (int)state);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
