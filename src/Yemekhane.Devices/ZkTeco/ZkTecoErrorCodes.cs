namespace Yemekhane.Devices.ZkTeco;

/// <summary>
/// ZKTeco cihazlari icin hata kodlari. Son ekler <see cref="Abstractions.DeviceErrorCodes"/>
/// siniflandirmasiyla bilerek ortaktir; boylece kart yukleme dongusu kopmus bir ZKTeco cihazini
/// SF300 ile ayni sekilde ele alir.
/// </summary>
public static class ZkTecoErrorCodes
{
    public const string ProtocolError = "ZK_PROTOCOL_ERROR";
    public const string Timeout = "ZK_TIMEOUT";
    public const string Disconnected = "ZK_DISCONNECTED";
    public const string ConnectFailed = "ZK_CONNECT_FAILED";
    public const string ConnectTimeout = "ZK_CONNECT_TIMEOUT";
    public const string InvalidResponse = "ZK_INVALID_RESPONSE";
    public const string HandshakeInvalidResponse = "ZK_HANDSHAKE_INVALID_RESPONSE";
    public const string EndpointInvalid = "ZK_ENDPOINT_INVALID";
    public const string NotConfigured = "ZK_SDK_NOT_CONFIGURED";

    /// <summary>
    /// Donanim dokumantasyonu §08: belgede karsiligi olmayan bir davranis tahmin edilemez.
    /// Bu kod, cihaz basinda dogrulama yapilana kadar islemin yapilamayacagini bildirir.
    /// </summary>
    public const string ValidationRequired = "ZK_DEVICE_VALIDATION_REQUIRED";
}
