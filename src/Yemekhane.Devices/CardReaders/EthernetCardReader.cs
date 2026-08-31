using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.CardReaders;

public sealed class EthernetCardReader : ICardReader
{
    private const int BufferSize = 256;
    private const int MaximumCardLength = 128;
    private static readonly IReadOnlySet<DeviceCapability> Capabilities =
        new HashSet<DeviceCapability> { DeviceCapability.ReadCard, DeviceCapability.Status };

    private readonly ITcpTransport _transport;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _readTimeout;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private int _connectionState = (int)DeviceConnectionState.Disconnected;
    private int _disposed;

    public EthernetCardReader(Guid id, string name, DeviceEndpoint endpoint, TimeSpan? connectTimeout = null,
        TimeSpan? readTimeout = null)
        : this(id, name, endpoint, CreateTransport(endpoint), connectTimeout, readTimeout)
    {
    }

    internal EthernetCardReader(Guid id, string name, DeviceEndpoint endpoint, ITcpTransport transport,
        TimeSpan? connectTimeout = null, TimeSpan? readTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transport);
        ValidateEndpoint(endpoint);
        ValidateTimeout(connectTimeout, nameof(connectTimeout));
        ValidateTimeout(readTimeout, nameof(readTimeout));

        Id = id;
        Name = name;
        Endpoint = endpoint;
        _transport = transport;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
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
            if (_transport.IsConnected)
            {
                SetConnectionState(DeviceConnectionState.Connected);
                return CreateDeviceInfo();
            }

            SetConnectionState(DeviceConnectionState.Connecting);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_connectTimeout);
            try
            {
                await _transport.ConnectAsync(timeout.Token).ConfigureAwait(false);
                if (!_transport.IsConnected)
                {
                    SetConnectionState(DeviceConnectionState.Faulted);
                    throw new DeviceConnectionException(Name, $"{Endpoint.IpAddress}:{Endpoint.IpPort} TCP bağlantısı kurulamadı.",
                        "TCP_CONNECT_FAILED");
                }

                SetConnectionState(DeviceConnectionState.Connected);
                return CreateDeviceInfo();
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                await CloseAfterFailedConnectAsync().ConfigureAwait(false);
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw new DeviceConnectionException(Name, $"{Endpoint.IpAddress}:{Endpoint.IpPort} TCP bağlantısı zaman aşımına uğradı.",
                    "TCP_CONNECT_TIMEOUT", exception);
            }
            catch (OperationCanceledException)
            {
                await CloseAfterFailedConnectAsync().ConfigureAwait(false);
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw;
            }
            catch (DeviceConnectionException)
            {
                await CloseAfterFailedConnectAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception exception)
            {
                await CloseAfterFailedConnectAsync().ConfigureAwait(false);
                SetConnectionState(DeviceConnectionState.Faulted);
                throw new DeviceConnectionException(Name, $"{Endpoint.IpAddress}:{Endpoint.IpPort} TCP bağlantısı kurulamadı.",
                    "TCP_CONNECT_FAILED", exception);
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

            try
            {
                await _transport.DisconnectAsync(cancellationToken).ConfigureAwait(false);
                SetConnectionState(DeviceConnectionState.Disconnected);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                SetConnectionState(DeviceConnectionState.Faulted);
                throw new DeviceConnectionException(Name, "TCP bağlantısı kapatılamadı.", "TCP_DISCONNECT_FAILED", exception);
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
        if (state == DeviceConnectionState.Connected && !_transport.IsConnected)
        {
            state = DeviceConnectionState.Disconnected;
            SetConnectionState(state);
        }

        var message = state == DeviceConnectionState.Connected ? "TCP bağlantısı etkin." : "TCP bağlantısı etkin değil.";
        return Task.FromResult(new DeviceStatus(state, DateTimeOffset.UtcNow, message));
    }

    public async IAsyncEnumerable<CardReadEvent> ReadCardsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ConnectionState != DeviceConnectionState.Connected || !_transport.IsConnected)
        {
            SetConnectionState(DeviceConnectionState.Disconnected);
            throw CreateDisconnectedException();
        }

        var bytes = new byte[BufferSize];
        var frame = new StringBuilder();
        var discardingInvalidFrame = false;

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
                    if (!discardingInvalidFrame && TryParseCardNumber(frame, out var cardNumber))
                    {
                        yield return new CardReadEvent(cardNumber, DateTimeOffset.UtcNow, Name);
                    }

                    frame.Clear();
                    discardingInvalidFrame = false;
                    continue;
                }

                if (discardingInvalidFrame)
                {
                    continue;
                }

                if (value < 0x20 || value > 0x7e || frame.Length >= MaximumCardLength)
                {
                    frame.Clear();
                    discardingInvalidFrame = true;
                    continue;
                }

                frame.Append((char)value);
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
            try
            {
                await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Dispose must release the socket even when graceful disconnect fails.
            }

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
            if (!_transport.IsConnected)
            {
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw CreateDisconnectedException(exception);
            }

            throw new DeviceConnectionException(Name, "Ethernet kart okuma zaman aşımına uğradı.",
                "TCP_READ_TIMEOUT", exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!_transport.IsConnected)
            {
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw CreateDisconnectedException(exception);
            }

            SetConnectionState(DeviceConnectionState.Faulted);
            throw new DeviceConnectionException(Name, "Ethernet bağlantısından kart okunamadı.",
                "TCP_READ_FAILED", exception);
        }
    }

    private async Task CloseAfterFailedConnectAsync()
    {
        try
        {
            await _transport.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original connection failure.
        }
    }

    private static bool TryParseCardNumber(StringBuilder frame, out string cardNumber)
    {
        cardNumber = frame.ToString().Trim();
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

    private static TcpClientTransport CreateTransport(DeviceEndpoint endpoint)
    {
        ValidateEndpoint(endpoint);
        return new TcpClientTransport(endpoint.IpAddress!, endpoint.IpPort!.Value);
    }

    private static void ValidateEndpoint(DeviceEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!string.Equals(endpoint.ConnectionType, "Ethernet", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Bağlantı türü Ethernet olmalıdır.", nameof(endpoint));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.IpAddress);
        if (Uri.CheckHostName(endpoint.IpAddress) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("Geçerli bir IP adresi veya DNS adı belirtilmelidir.", nameof(endpoint));
        }

        if (endpoint.IpPort is null or <= IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint), "TCP portu 1 ile 65535 arasında olmalıdır.");
        }
    }

    private static void ValidateTimeout(TimeSpan? timeout, string parameterName)
    {
        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Zaman aşımı sıfırdan büyük olmalıdır.");
        }
    }

    private static DeviceInfo CreateDeviceInfo() => new("Ethernet Card Reader", null, null, Capabilities);

    private DeviceConnectionException CreateDisconnectedException(Exception? innerException = null) => new(Name,
        $"{Endpoint.IpAddress}:{Endpoint.IpPort} TCP bağlantısı kapandı.", "TCP_DISCONNECTED", innerException);

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private void SetConnectionState(DeviceConnectionState state) =>
        Volatile.Write(ref _connectionState, (int)state);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
