using System.Net;
using System.Runtime.CompilerServices;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.ZkTeco;

/// <summary>
/// ZKTeco SC403 standalone gecis kontrol terminali icin adaptor.
///
/// Cihaz dogrulamasi (donanim dokumantasyonu §01): 125 kHz RFID proximity okuyucu, TCP/IP + RS485 +
/// USB-Host haberlesme, 30.000 kart / 50.000 islem kapasitesi, DC 12 V.
///
/// Adaptor yasam dongusu, zaman asimi, yeniden deneme, yetenek denetimi ve yanit dogrulamasindan
/// sorumludur; SDK cagrilarinin kendisi <see cref="IZkTecoSdk"/> arkasindadir. Bu ayrim, SDK
/// baglamasi cihaz basinda dogrulanana kadar adaptorun tam olarak test edilebilmesini saglar.
///
/// SC403 tek basina bir turnike DEGILDIR; kapi rolesi/Wiegand cikisi uzerinden bir turnikeyi surer.
/// Turnike surumu icin <see cref="Sc403AccessController"/> kullanilir.
/// </summary>
public class Sc403Adapter : ICardReader, IDeviceCapabilityProvider
{
    /// <summary>SC403 ID kart kapasitesi (uretici urun sayfasi §01.2).</summary>
    public const int MaxCardCapacity = 30_000;

    /// <summary>SC403 islem kaydi kapasitesi (uretici urun sayfasi §01.2).</summary>
    public const int MaxTransactionCapacity = 50_000;

    /// <summary>ZKTeco standalone cihazlarinin yaygin TCP portu.</summary>
    public const int DefaultPort = 4370;

    private readonly IZkTecoSdk? _sdk;
    private readonly TimeSpan _operationTimeout;
    private readonly int _maxRetryCount;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private HashSet<DeviceCapability> _capabilities = [];
    private int _connectionState = (int)DeviceConnectionState.Disconnected;
    private int _disposed;

    public Sc403Adapter(Guid id, string name, DeviceEndpoint endpoint, IZkTecoSdk? sdk = null,
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
        _sdk = sdk;
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
        var sdk = RequireSdk();
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            SetConnectionState(DeviceConnectionState.Connecting);
            try
            {
                if (!sdk.IsConnected)
                {
                    await ExecuteAsync(token => sdk.ConnectAsync(Endpoint, token), "bağlantı", cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!sdk.IsConnected)
                {
                    throw new DeviceConnectionException(Name, "SC403 SDK bağlantısı kurulamadı.",
                        ZkTecoErrorCodes.ConnectFailed);
                }

                var info = await ExecuteAsync(sdk.GetDeviceInfoAsync, "cihaz bilgisi", cancellationToken)
                    .ConfigureAwait(false);
                info = ValidateDeviceInfo(info, ZkTecoErrorCodes.HandshakeInvalidResponse);
                _capabilities = new HashSet<DeviceCapability>(info.Capabilities);
                SetConnectionState(DeviceConnectionState.Connected);
                return info;
            }
            catch (OperationCanceledException)
            {
                await CloseAfterFailureAsync(sdk).ConfigureAwait(false);
                SetConnectionState(DeviceConnectionState.Disconnected);
                throw;
            }
            catch (DeviceConnectionException)
            {
                await CloseAfterFailureAsync(sdk).ConfigureAwait(false);
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
        if (IsDisposed) return;

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sdk is not null && _sdk.IsConnected)
            {
                await ExecuteAsync(_sdk.DisconnectAsync, "bağlantıyı kapatma", cancellationToken, allowRetry: false)
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
        var info = await ExecuteCommandAsync(RequireSdk().GetDeviceInfoAsync, "cihaz bilgisi", cancellationToken)
            .ConfigureAwait(false);
        info = ValidateDeviceInfo(info, ZkTecoErrorCodes.InvalidResponse);
        _capabilities = new HashSet<DeviceCapability>(info.Capabilities);
        return info;
    }

    public async Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var sdk = RequireSdk();
        if (ConnectionState != DeviceConnectionState.Connected || !sdk.IsConnected)
        {
            ResetConnection();
            return new DeviceStatus(DeviceConnectionState.Disconnected, DateTimeOffset.UtcNow, "SC403 bağlı değil.");
        }

        EnsureCapability(DeviceCapability.Status);
        var status = await ExecuteCommandAsync(sdk.GetStatusAsync, "durum", cancellationToken).ConfigureAwait(false);
        if (status is null || status.CheckedAt == default || !Enum.IsDefined(status.State))
        {
            throw InvalidResponse("SC403 durum yanıtı geçersiz.");
        }

        if (status.State != DeviceConnectionState.Connected)
        {
            SetConnectionState(status.State);
        }

        return status;
    }

    /// <summary>
    /// Gercek zamanli kart olaylari (Manual: RegEvent + ReadRTLog/GetRTLog).
    ///
    /// Kart okuma akisi zaman asimina UGRAMAZ: sessiz bir yemekhanede iki okutma arasinda dakikalar
    /// gecebilir ve bunu baglanti hatasi saymak calisan cihazi surekli yeniden baglatirdi.
    /// Baglanti sagligi ayrica <see cref="GetStatusAsync"/> yoklamasiyla izlenir.
    /// </summary>
    public async IAsyncEnumerable<CardReadEvent> ReadCardsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        EnsureAvailable(DeviceCapability.ReadCard);
        var sdk = RequireSdk();
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = sdk.ReadRealTimeCardsAsync(streamCancellation.Token)
            .GetAsyncEnumerator(streamCancellation.Token);
        try
        {
            while (await MoveNextAsync(enumerator).ConfigureAwait(false))
            {
                var card = enumerator.Current;
                if (card is null || string.IsNullOrWhiteSpace(card.CardNumber) || card.Timestamp == default)
                {
                    throw InvalidResponse("SC403 kart okuma yanıtı geçersiz.");
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

    public Task<DeviceCommandResult> SendUserAsync(DeviceUser user, CancellationToken cancellationToken)
    {
        ValidateUser(user);
        return ExecuteResultAsync(DeviceCapability.SendUser,
            token => RequireSdk().SetUserInfoAsync(user, token), "kullanıcı gönderme", cancellationToken);
    }

    public Task<DeviceCommandResult> SendCardAsync(string cardNumber, string externalUserId,
        CancellationToken cancellationToken)
    {
        ValidateCardArguments(cardNumber, externalUserId);
        return ExecuteResultAsync(DeviceCapability.SendCard,
            token => RequireSdk().SetCardNumberAsync(cardNumber, externalUserId, token), "kart gönderme",
            cancellationToken);
    }

    public Task<DeviceCommandResult> SyncUserAsync(DeviceUser user, CancellationToken cancellationToken)
    {
        ValidateUser(user);
        return ExecuteResultAsync(DeviceCapability.SyncUser,
            token => RequireSdk().SetUserInfoAsync(user, token), "kullanıcı eşitleme", cancellationToken);
    }

    public Task<DeviceCommandResult> SyncCardAsync(string cardNumber, string externalUserId,
        CancellationToken cancellationToken)
    {
        ValidateCardArguments(cardNumber, externalUserId);
        return ExecuteResultAsync(DeviceCapability.SyncCard,
            token => RequireSdk().SetCardNumberAsync(cardNumber, externalUserId, token), "kart eşitleme",
            cancellationToken);
    }

    public Task<DeviceCommandResult> DeleteCardAsync(string cardNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        ZkTecoCardNumber.Validate(cardNumber);
        return ExecuteResultAsync(DeviceCapability.DeleteCard,
            token => RequireSdk().DeleteUserInfoAsync(cardNumber, token), "kart silme", cancellationToken);
    }

    public async Task<DeviceUser?> ReadUserAsync(string externalUserId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);
        EnsureAvailable(DeviceCapability.ReadUser);
        var user = await ExecuteCommandAsync(token => RequireSdk().GetUserInfoAsync(externalUserId, token),
            "kullanıcı okuma", cancellationToken).ConfigureAwait(false);
        if (user is not null && (!string.Equals(user.ExternalId, externalUserId, StringComparison.Ordinal) ||
                                 string.IsNullOrWhiteSpace(user.Name)))
        {
            throw InvalidResponse("SC403 kullanıcı okuma yanıtı geçersiz.");
        }

        return user;
    }

    public async Task<string?> ReadCardAsync(string cardNumber, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        ZkTecoCardNumber.Validate(cardNumber);
        EnsureAvailable(DeviceCapability.ReadCard);
        var externalUserId = await ExecuteCommandAsync(
            token => RequireSdk().GetUserIdByCardAsync(cardNumber, token), "kart okuma", cancellationToken)
            .ConfigureAwait(false);
        if (externalUserId is not null && string.IsNullOrWhiteSpace(externalUserId))
        {
            throw InvalidResponse("SC403 kart okuma yanıtı geçersiz.");
        }

        return externalUserId;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await _lifecycleLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (_sdk is not null)
            {
                await CloseAfterFailureAsync(_sdk).ConfigureAwait(false);
                var disposeTask = _sdk.DisposeAsync().AsTask();
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

        GC.SuppressFinalize(this);
    }

    private protected async Task<DeviceCommandResult> ExecuteResultAsync(DeviceCapability capability,
        Func<CancellationToken, Task<DeviceCommandResult?>> action, string operation,
        CancellationToken cancellationToken)
    {
        EnsureAvailable(capability);
        var result = await ExecuteCommandAsync(action, operation, cancellationToken).ConfigureAwait(false);
        if (result is null || string.IsNullOrWhiteSpace(result.Message))
        {
            throw InvalidResponse($"SC403 {operation} yanıtı geçersiz.");
        }

        return result;
    }

    private protected IZkTecoSdk RequireSdk() => _sdk ?? throw new DeviceConnectionException(Name,
        "SC403 SDK bağlaması yapılandırılmadı; belgelenmiş bir IZkTecoSdk uygulaması gereklidir.",
        ZkTecoErrorCodes.NotConfigured);

    private protected async Task<T> ExecuteCommandAsync<T>(Func<CancellationToken, Task<T>> action, string operation,
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
                throw new DeviceConnectionException(Name, $"SC403 {operation} işlemi zaman aşımına uğradı.",
                    ZkTecoErrorCodes.Timeout, exception);
            }
            catch (ZkTecoProtocolException exception) when (exception.IsTransient && attempt < attempts)
            {
                // SDK bu islemin yeniden denenmesini acikca guvenli isaretledi.
            }
            catch (ZkTecoProtocolException exception)
            {
                throw new DeviceConnectionException(Name, $"SC403 {operation} işlemi başarısız: {exception.Message}",
                    exception.ErrorCode, exception);
            }
            catch (DeviceConnectionException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new DeviceConnectionException(Name, $"SC403 {operation} işlemi başarısız.",
                    ZkTecoErrorCodes.ProtocolError, exception);
            }
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> action, string operation,
        CancellationToken cancellationToken, bool allowRetry = true) =>
        await ExecuteAsync(async token => { await action(token).ConfigureAwait(false); return true; }, operation,
            cancellationToken, allowRetry).ConfigureAwait(false);

    private async Task<bool> MoveNextAsync(IAsyncEnumerator<CardReadEvent> enumerator)
    {
        try
        {
            return await enumerator.MoveNextAsync().ConfigureAwait(false);
        }
        catch (ZkTecoProtocolException exception)
        {
            throw new DeviceConnectionException(Name, $"SC403 kart okuma işlemi başarısız: {exception.Message}",
                exception.ErrorCode, exception);
        }
    }

    private protected void EnsureAvailable(DeviceCapability capability)
    {
        ThrowIfDisposed();
        EnsureConnected();
        EnsureCapability(capability);
    }

    private void EnsureConnected()
    {
        var sdk = RequireSdk();
        if (ConnectionState != DeviceConnectionState.Connected || !sdk.IsConnected)
        {
            ResetConnection();
            throw new DeviceConnectionException(Name, "SC403 bağlı değil.", ZkTecoErrorCodes.Disconnected);
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
            throw new DeviceConnectionException(Name, "SC403 cihaz bilgisi yanıtı geçersiz.", errorCode);
        }

        return info;
    }

    private static void ValidateUser(DeviceUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(user.ExternalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(user.Name);
        if (!string.IsNullOrWhiteSpace(user.CardNumber)) ZkTecoCardNumber.Validate(user.CardNumber);
    }

    private static void ValidateCardArguments(string cardNumber, string externalUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);
        ZkTecoCardNumber.Validate(cardNumber);
    }

    /// <summary>
    /// SC403 haberlesmesi TCP/IP, RS485 veya USB-Host olabilir (§01.2). Bu adaptor TCP/IP kullanir;
    /// RS485 ve USB-Host cihaz basinda dogrulanmadan desteklendigi iddia edilemez (§08).
    /// </summary>
    private static void ValidateEndpoint(DeviceEndpoint endpoint)
    {
        if (!string.Equals(endpoint.ConnectionType, "Ethernet", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("SC403 bağlantı türü Ethernet olmalıdır.", nameof(endpoint));
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

    private protected DeviceConnectionException InvalidResponse(string message) =>
        new(Name, message, ZkTecoErrorCodes.InvalidResponse);

    private async Task CloseAfterFailureAsync(IZkTecoSdk sdk)
    {
        using var timeout = new CancellationTokenSource(_operationTimeout);
        try
        {
            await sdk.DisconnectAsync(timeout.Token).WaitAsync(_operationTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Asil hata veya elden cikarma korunur.
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

    private protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    private void SetConnectionState(DeviceConnectionState state) => Volatile.Write(ref _connectionState, (int)state);
    private protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, this);
}
