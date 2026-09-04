using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.ZkTeco;

/// <summary>
/// ZKTeco Standalone SDK sinirini temsil eder. Uyeler, ZKTeco Standalone SDK Development Manual
/// (v2.1 Rev A.2 / v2.2 Rev A.3) dokumaninda ADI GECEN fonksiyonlarla birebir eslesir; burada
/// belgede bulunmayan hicbir fonksiyon tanimlanmaz.
///
/// Bu arayuz kasitli olarak SOYUTTUR. Gercek baglama <c>zkemkeeper.dll</c> COM bileseni uzerinden
/// yapilir; bu bilesen 32-bit ActiveX'tir, makineye <c>regsvr32</c> ile kaydedilmesi gerekir ve
/// bu depoda bulunmamaktadir. Dolayisiyla somut uygulama, cihaz basindaki dogrulamadan once
/// yazilamaz (bkz. donanim dokumantasyonu §08: tahmin etmek yasak).
///
/// Belgede karsiligi olmayan her davranis <see cref="ZkTecoProtocolException"/> ile
/// DEVICE_VALIDATION_REQUIRED olarak bildirilir; sessizce basarili sayilmaz.
/// </summary>
public interface IZkTecoSdk : IAsyncDisposable
{
    /// <summary>SDK oturumunun canli olup olmadigi. Manual: Device connection.</summary>
    bool IsConnected { get; }

    /// <summary>Manual: Connect_Net (TCP/IP), Connect_Com (RS485), Connect_USB.</summary>
    Task ConnectAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken);

    /// <summary>Manual: Disconnect.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Manual: GetDeviceInfo / GetSerialNumber / GetProductCode / GetFirmwareVersion / GetSDKVersion.
    /// Cihazin gercekte destekledigi yetenekler burada bildirilir.
    /// </summary>
    Task<DeviceInfo?> GetDeviceInfoAsync(CancellationToken cancellationToken);

    /// <summary>Manual: GetDeviceStatus.</summary>
    Task<DeviceStatus?> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Manual: RegEvent + ReadRTLog/GetRTLog ile gercek zamanli kart olaylari.</summary>
    IAsyncEnumerable<CardReadEvent> ReadRealTimeCardsAsync(CancellationToken cancellationToken);

    /// <summary>Manual: SetUserInfo / SSR_SetUserInfo.</summary>
    Task<DeviceCommandResult?> SetUserInfoAsync(DeviceUser user, CancellationToken cancellationToken);

    /// <summary>Manual: SetStrCardNumber (+ SSR_SetUserInfo ile kullanici eslesmesi).</summary>
    Task<DeviceCommandResult?> SetCardNumberAsync(string cardNumber, string externalUserId,
        CancellationToken cancellationToken);

    /// <summary>Manual: DeleteUserInfoEx. Iptal edilen kart cihazda kalirsa gecise devam eder.</summary>
    Task<DeviceCommandResult?> DeleteUserInfoAsync(string cardNumber, CancellationToken cancellationToken);

    /// <summary>Manual: GetUserInfo / SSR_GetUserInfo / GetUserIDByPIN2.</summary>
    Task<DeviceUser?> GetUserInfoAsync(string externalUserId, CancellationToken cancellationToken);

    /// <summary>Manual: GetUserInfoByCard — kart numarasindan kullanici kimligi.</summary>
    Task<string?> GetUserIdByCardAsync(string cardNumber, CancellationToken cancellationToken);
}

/// <summary>
/// ZKTeco SDK duzeyinde bir hatayi ve istegin yeniden denenmesinin guvenli olup olmadigini bildirir.
/// Yanlis siniflandirma pahalidir: kalici hatayi gecici saymak sonsuz denemeye, tersi ise anlik bir
/// mesguliyette kart yuklemesinin kalici basarisiz sayilmasina yol acar.
/// </summary>
public sealed class ZkTecoProtocolException(string message, bool isTransient = false,
    string errorCode = ZkTecoErrorCodes.ProtocolError, Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool IsTransient { get; } = isTransient;
    public string ErrorCode { get; } = string.IsNullOrWhiteSpace(errorCode)
        ? ZkTecoErrorCodes.ProtocolError
        : errorCode;
}
