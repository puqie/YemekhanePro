using System.Net;
using System.Runtime.CompilerServices;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Sf300;

public sealed class SF300Adapter : IAccessController
{
    private const string NotConfiguredCode = "SF300_PROTOCOL_NOT_CONFIGURED";
    private readonly ISf300Protocol? _protocol;
    private readonly TimeSpan _operationTimeout;
    private readonly int _maxRetryCount;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private HashSet<DeviceCapability> _capabilities = [];
    private int _connectionState = (int)DeviceConnectionState.Disconnected;
    private int _disposed;

    public SF300Adapter(Guid id, string name, DeviceEndpoint endpoint, ISf300Protocol? protocol = null,
        TimeSpan? operationTimeout = null, int maxRetryCount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateEndpoint(endpoint);
        if (operationTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(operationTimeout), "Zaman aşımı sıfırdan büyük olmalıdır.");
        }

        if (maxRetryCount is < 0 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetryCount), "Yeniden deneme sayısı 0 ile 10 arasında olmalıdır.");
        }

        Id = id;
        Name = name;
        Endpoint = endpoint;
        _protocol = protocol;
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(10);
        _maxRetryCount = maxRetryCount;
    }

    public Guid Id { get; }
    public string Name { get; }
    public DeviceEndpoint Endpoint { get; }
    public DeviceConnectionState ConnectionState => (DeviceConnectionState)Volatile.Read(ref _connectionState);
    public IReadOnlySet<DeviceCapability> Capabilities => _capabilities;

    public async Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var protocol = RequireProtocol();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            SetConnectionState(DeviceConnectionState.Connecting);
            try
            {
                if (!protocol.IsConnected)
                {
                    await ExecuteAsync(token => protocol.ConnectAsync(Endpoint, token), "bağlantı", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!protocol.IsConnected)
                {
                    throw new DeviceConnectionException(Name, "SF300 protokol bağlantısı kurulamadı.", "SF300_CONNECT_FAILED");
                }

                var info = await ExecuteAsync(protocol.GetDeviceInfoAsync, "handshake", cancellationToken)
                    .ConfigureAwait(false);
                info = ValidateDeviceInfo(info, "SF300_HANDSHAKE_INVALID_RESPONSE");
                _capabilities = new HashSet<DeviceCapability>(info.Capabilities);
                SetConnectionState(DeviceConnectionState.Connected);
                return info;
            }
            catch (OperationCanceledException)
            {
                await CloseAfterFailureAsync(protocol).ConfigureAwait(false);
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw;
            }
            catch (DeviceConnectionException)
            {
                await CloseAfterFailureAsync(protocol).ConfigureAwait(false);
                SetConnectionState(DeviceConnectionState.Faulted);
                throw;
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
            if (_protocol is not null && _protocol.IsConnected)
            {
                await ExecuteAsync(_protocol.DisconnectAsync, "bağlantıyı kapatma", cancellationToken, allowRetry: false)
                    .ConfigureAwait(false);
            }

            ResetConnection();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task<DeviceInfo> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        EnsureAvailable(DeviceCapability.DeviceInfo);
        var info = await ExecuteCommandAsync(RequireProtocol().GetDeviceInfoAsync, "cihaz bilgisi", cancellationToken)
            .ConfigureAwait(false);
        info = ValidateDeviceInfo(info, "SF300_INVALID_RESPONSE");
        _capabilities = new HashSet<DeviceCapability>(info.Capabilities);
        return info;
    }

    public async Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var protocol = RequireProtocol();
        if (ConnectionState != DeviceConnectionState.Connected || !protocol.IsConnected)
        {
            ResetConnection();
            return new DeviceStatus(DeviceConnectionState.Disconnected, DateTimeOffset.UtcNow, "SF300 bağlı değil.");
        }

        EnsureCapability(DeviceCapability.Status);
        var status = await ExecuteCommandAsync(protocol.GetStatusAsync, "durum", cancellationToken).ConfigureAwait(false);
        if (status is null || status.CheckedAt == default || !Enum.IsDefined(status.State))
        {
            throw InvalidResponse("SF300 durum yanıtı geçersiz.");
        }

        if (status.State != DeviceConnectionState.Connected)
        {
            SetConnectionState(status.State);
        }

        return status;
    }

    public async IAsyncEnumerable<CardReadEvent> ReadCardsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureAvailable(DeviceCapability.ReadCard);
        var protocol = RequireProtocol();
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = protocol.ReadCardsAsync(streamCancellation.Token).GetAsyncEnumerator(streamCancellation.Token);
        try
        {
            while (await MoveNextWithTimeoutAsync(enumerator, streamCancellation, cancellationToken).ConfigureAwait(false))
            {
                var card = enumerator.Current;
                if (card is null || string.IsNullOrWhiteSpace(card.CardNumber) || card.Timestamp == default)
                {
                    throw InvalidResponse("SF300 kart okuma yanıtı geçersiz.");
                }

                yield return card;
            }
        }
        finally
        {
            Task? disposeTask = null;
            try
            {
                disposeTask = enumerator.DisposeAsync().AsTask();
                await disposeTask.WaitAsync(_operationTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                if (disposeTask is not null) ObserveFault(disposeTask);
            }
        }
    }

    public Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
        ExecuteResultAsync(DeviceCapability.GrantAccess,
            token => RequireProtocol().GrantAccessAsync(direction, token), "erişim izni", cancellationToken);

    public Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
        ExecuteResultAsync(DeviceCapability.DenyAccess,
            token => RequireProtocol().DenyAccessAsync(direction, token), "erişim reddi", cancellationToken);

    public Task<DeviceCommandResult> SendUserAsync(DeviceUser user, CancellationToken cancellationToken)
    {
        ValidateUser(user);
        return ExecuteResultAsync(DeviceCapability.SendUser,
            token => RequireProtocol().SendUserAsync(user, token), "kullanıcı gönderme", cancellationToken);
    }

    public Task<DeviceCommandResult> SendCardAsync(string cardNumber, string externalUserId,
        CancellationToken cancellationToken)
    {
        ValidateCardArguments(cardNumber, externalUserId);
        return ExecuteResultAsync(DeviceCapability.SendCard,
            token => RequireProtocol().SendCardAsync(cardNumber, externalUserId, token), "kart gönderme", cancellationToken);
    }

    public Task<DeviceCommandResult> SyncUserAsync(DeviceUser user, CancellationToken cancellationToken)
    {
        ValidateUser(user);
        return ExecuteResultAsync(DeviceCapability.SyncUser,
            token => RequireProtocol().SyncUserAsync(user, token), "kullanıcı eşitleme", cancellationToken);
    }

    public Task<DeviceCommandResult> SyncCardAsync(string cardNumber, string externalUserId,
        CancellationToken cancellationToken)
    {
        ValidateCardArguments(cardNumber, externalUserId);
        return ExecuteResultAsync(DeviceCapability.SyncCard,
            token => RequireProtocol().SyncCardAsync(cardNumber, externalUserId, token), "kart eşitleme", cancellationToken);
    }

    public Task<DeviceCommandResult> DeleteCardAsync(string cardNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        return ExecuteResultAsync(DeviceCapability.DeleteCard,
            token => RequireProtocol().DeleteCardAsync(cardNumber, token), "kart silme", cancellationToken);
    }

    public async Task<DeviceUser?> ReadUserAsync(string externalUserId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);
        EnsureAvailable(DeviceCapability.ReadUser);
        var user = await ExecuteCommandAsync(token => RequireProtocol().ReadUserAsync(externalUserId, token),
            "kullanıcı okuma", cancellationToken).ConfigureAwait(false);
        if (user is not null && (!string.Equals(user.ExternalId, externalUserId, StringComparison.Ordinal) ||
                                 string.IsNullOrWhiteSpace(user.Name)))
        {
            throw InvalidResponse("SF300 kullanıcı okuma yanıtı geçersiz.");
        }

        return user;
    }

    public async Task<string?> ReadCardAsync(string cardNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        EnsureAvailable(DeviceCapability.ReadCard);
        var externalUserId = await ExecuteCommandAsync(token => RequireProtocol().ReadCardAsync(cardNumber, token),
            "kart okuma", cancellationToken).ConfigureAwait(false);
        if (externalUserId is not null && string.IsNullOrWhiteSpace(externalUserId))
        {
            throw InvalidResponse("SF300 kart okuma yanıtı geçersiz.");
        }

        return externalUserId;
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
            if (_protocol is not null)
            {
                await CloseAfterFailureAsync(_protocol).ConfigureAwait(false);
                var disposeTask = _protocol.DisposeAsync().AsTask();
                try
                {
                    await disposeTask.WaitAsync(_operationTimeout).ConfigureAwait(false);
                }
                catch
                {
                    ObserveFault(disposeTask);
                }
            }

            ResetConnection();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task<DeviceCommandResult> ExecuteResultAsync(DeviceCapability capability,
        Func<CancellationToken, Task<DeviceCommandResult?>> action, string operation, CancellationToken cancellationToken)
    {
        EnsureAvailable(capability);
        var result = await ExecuteCommandAsync(action, operation, cancellationToken).ConfigureAwait(false);
        if (result is null || string.IsNullOrWhiteSpace(result.Message))
        {
            throw InvalidResponse($"SF300 {operation} yanıtı geçersiz.");
        }

        return result;
    }

    private async Task<T> ExecuteCommandAsync<T>(Func<CancellationToken, Task<T>> action, string operation,
        CancellationToken cancellationToken)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            return await ExecuteAsync(action, operation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, string operation,
        CancellationToken cancellationToken, bool allowRetry = true)
    {
        var attempts = allowRetry ? _maxRetryCount + 1 : 1;
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_operationTimeout);
            try
            {
                return await action(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DeviceConnectionException(Name, $"SF300 {operation} işlemi zaman aşımına uğradı.",
                    "SF300_TIMEOUT", exception);
            }
            catch (Sf300ProtocolException exception) when (exception.IsTransient && attempt < attempts)
            {
                // A protocol implementation explicitly marked this operation safe to retry.
            }
            catch (Sf300ProtocolException exception)
            {
                throw new DeviceConnectionException(Name, $"SF300 {operation} işlemi başarısız: {exception.Message}",
                    exception.ErrorCode, exception);
            }
            catch (DeviceConnectionException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new DeviceConnectionException(Name, $"SF300 {operation} işlemi başarısız.",
                    "SF300_PROTOCOL_ERROR", exception);
            }
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, string operation,
        CancellationToken cancellationToken, bool allowRetry = true) =>
        await ExecuteAsync(async token => { await action(token).ConfigureAwait(false); return true; }, operation,
            cancellationToken, allowRetry).ConfigureAwait(false);

    private async Task<bool> MoveNextWithTimeoutAsync(IAsyncEnumerator<CardReadEvent> enumerator,
        CancellationTokenSource streamCancellation, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_operationTimeout);
        var moveTask = enumerator.MoveNextAsync().AsTask();
        try
        {
            return await moveTask.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await streamCancellation.CancelAsync().ConfigureAwait(false);
            ObserveFault(moveTask);

            throw new DeviceConnectionException(Name, "SF300 kart bekleme işlemi zaman aşımına uğradı.",
                "SF300_TIMEOUT", exception);
        }
        catch (Sf300ProtocolException exception)
        {
            throw new DeviceConnectionException(Name, $"SF300 kart okuma işlemi başarısız: {exception.Message}",
                exception.ErrorCode, exception);
        }
    }

    private ISf300Protocol RequireProtocol() => _protocol ?? throw new DeviceConnectionException(Name,
        "SF300 protokolü yapılandırılmadı; belgelenmiş bir ISf300Protocol uygulaması gereklidir.", NotConfiguredCode);

    private void EnsureAvailable(DeviceCapability capability)
    {
        ThrowIfDisposed();
        EnsureConnected();
        EnsureCapability(capability);
    }

    private void EnsureConnected()
    {
        var protocol = RequireProtocol();
        if (ConnectionState != DeviceConnectionState.Connected || !protocol.IsConnected)
        {
            ResetConnection();
            throw new DeviceConnectionException(Name, "SF300 bağlı değil.", "SF300_DISCONNECTED");
        }
    }

    private void EnsureCapability(DeviceCapability capability)
    {
        if (!_capabilities.Contains(capability))
        {
            throw new DeviceCapabilityException(Name, capability);
        }
    }

    private DeviceInfo ValidateDeviceInfo(DeviceInfo? info, string errorCode)
    {
        if (info is null || string.IsNullOrWhiteSpace(info.Model) || info.Capabilities is null ||
            info.Capabilities.Any(capability => !Enum.IsDefined(capability)))
        {
            throw new DeviceConnectionException(Name, "SF300 cihaz bilgisi yanıtı geçersiz.", errorCode);
        }

        return info;
    }

    private static void ValidateUser(DeviceUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(user.ExternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(user.Name);
    }

    private static void ValidateCardArguments(string cardNumber, string externalUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);
    }

    private static void ValidateEndpoint(DeviceEndpoint endpoint)
    {
        if (!string.Equals(endpoint.ConnectionType, "Ethernet", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("SF300 bağlantı türü Ethernet olmalıdır.", nameof(endpoint));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint.IpAddress);
        if (Uri.CheckHostName(endpoint.IpAddress) == UriHostNameType.Unknown)
        {
            throw new ArgumentException("Geçerli bir IP adresi veya DNS adı belirtilmelidir.", nameof(endpoint));
        }

        if (endpoint.IpPort is null or <= IPEndPoint.MinPort or > IPEndPoint.MaxPort)
        {
            throw new ArgumentOutOfRangeException(nameof(endpoint), "TCP portu açıkça belirtilmeli ve geçerli olmalıdır.");
        }
    }

    private DeviceConnectionException InvalidResponse(string message) =>
        new(Name, message, "SF300_INVALID_RESPONSE");

    private async Task CloseAfterFailureAsync(ISf300Protocol protocol)
    {
        using var timeout = new CancellationTokenSource(_operationTimeout);
        try
        {
            await protocol.DisconnectAsync(timeout.Token).WaitAsync(_operationTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original failure or complete disposal.
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private void ResetConnection()
    {
        _capabilities = [];
        SetConnectionState(DeviceConnectionState.Disconnected);
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private void SetConnectionState(DeviceConnectionState state) => Volatile.Write(ref _connectionState, (int)state);
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
