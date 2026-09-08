namespace Yemekhane.Devices.ZkTeco.Protocol;

/// <summary>
/// ZKTeco standalone (ZEM tabanli) cihazlarin tel protokolu komut kodlari.
///
/// <para>
/// KAYNAK: iki bagimsiz, yaygin kullanilan acik kaynak uygulama -- pyzk (Python) ve
/// node-zklib (JavaScript). Ikisi de ayni kodlari ve ayni paket bicimini kullanir; bu dosya
/// yalnizca ikisinde de gecen kodlari icerir. Bu, "SDK kilavuzunda olmayani uydurma"
/// (donanim dokumantasyonu §08) kuralinin ruhuna uygundur: tahmin degil, binlerce cihazda
/// dogrulanmis referans davranis kopyalanir.
/// </para>
/// <para>
/// Uretici SDK'si (zkemkeeper.dll) ayni protokolu konusur; oradaki fonksiyon adlariyla
/// esleme: Connect_Net → CONNECT(+AUTH), Disconnect → EXIT, GetFirmwareVersion → GET_VERSION,
/// GetSerialNumber/GetDeviceInfo → OPTIONS_RRQ, GetDeviceStatus → GET_FREE_SIZES,
/// RegEvent → REG_EVENT, SetUserInfo/SetStrCardNumber → USER_WRQ, DeleteUserInfoEx → DELETE_USER,
/// GetAllUserInfo → DATA_WRRQ(USERTEMP_RRQ), ACUnlock → UNLOCK.
/// </para>
/// </summary>
public static class ZkCommands
{
    public const ushort Connect = 1000;
    public const ushort Exit = 1001;
    public const ushort EnableDevice = 1002;
    public const ushort DisableDevice = 1003;
    public const ushort RefreshData = 1013;
    public const ushort GetVersion = 1100;
    public const ushort Auth = 1102;

    public const ushort OptionsRead = 11;
    public const ushort OptionsWrite = 12;
    public const ushort UserWrite = 8;
    public const ushort UserTemplateRead = 9;
    public const ushort DeleteUser = 18;
    public const ushort Unlock = 31;
    public const ushort GetFreeSizes = 50;
    public const ushort RegisterEvent = 500;

    public const ushort PrepareData = 1500;
    public const ushort Data = 1501;
    public const ushort FreeData = 1502;
    public const ushort DataWriteReadRequest = 1503;
    public const ushort ReadBuffer = 1504;

    public const ushort AckOk = 2000;
    public const ushort AckError = 2001;
    public const ushort AckData = 2002;
    public const ushort AckUnauthorized = 2005;

    /// <summary>DATA_WRRQ icin veri turu: kullanici tablosu.</summary>
    public const byte FunctionUser = 5;

    /// <summary>Gercek zamanli olay bayraklari (REG_EVENT verisi).</summary>
    public static class Events
    {
        public const uint AttendanceLog = 1;
        public const uint Finger = 1 << 1;
        public const uint EnrollUser = 1 << 2;
        public const uint EnrollFinger = 1 << 3;
        public const uint Button = 1 << 4;
        public const uint Unlock = 1 << 5;
        public const uint Verify = 1 << 7;
        public const uint FingerFeature = 1 << 8;
        public const uint Alarm = 1 << 9;

        /// <summary>
        /// Kart numarasi olayi (zkemkeeper: OnHIDNum). pyzk/node-zklib bu bayragi tanimlamaz;
        /// SAHADA OLCULDU (SC403/M, ZEM500, fw 6.60, 2026-09-08): her okutmada cihaz once bu
        /// olayi (veri 5 bayt: kart uint32 LE + 1 dolgu), ~1,3 sn sonra EF_ATTLOG gonderir.
        /// Kart numarasi dogrudan geldigi icin cihaz tablosuna bakmadan ve kayitsiz kartlar
        /// icin de calisir.
        /// </summary>
        public const uint CardNumber = 1 << 10;

        public const uint All = 0xFFFF;

        /// <summary>Uretim varsayilani: gecis kaydi + kart numarasi (yanki bastirma SDK'da).</summary>
        public const uint Default = AttendanceLog | CardNumber;
    }
}
