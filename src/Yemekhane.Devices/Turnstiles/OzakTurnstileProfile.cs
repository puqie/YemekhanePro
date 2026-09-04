using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Devices.Turnstiles;

/// <summary>
/// OZAK 720 E bel tipi (tripod) turnikenin FIZIKSEL surulme profili.
///
/// ONEMLI — bu bir protokol DEGILDIR ve kasitli olarak <see cref="IDevice"/> uygulamaz.
/// Uretici teknik dokumanina gore (donanim dokumantasyonu §04.3) 720 E kontrol girisi
/// "kuru kontak veya TTL/CMOS, 5-48 V" seklindedir. Yani turnikenin IP adresi, seri portu veya
/// komut kumesi YOKTUR; bir role kontaginin kapanmasiyla acilir.
///
/// Bu yuzden turnike, gecis kontrol cihazinin (ornegin ZKTeco SC403) kapi rolesi cikisina baglanir
/// ve yazilim tarafindan SC403 uzerinden surulur. Turnikeye ayri bir ag protokolu uydurmak,
/// donanim dokumantasyonu §08 ile dogrudan celisirdi.
///
/// Burada tutulan degerler yalnizca uretici dokumaninda GECEN olculerdir; role darbe suresi gibi
/// saha ayarlari kurulumda dogrulanmalidir.
/// </summary>
public sealed record OzakTurnstileProfile
{
    /// <summary>Uretici model adi (§04.1).</summary>
    public const string Model = "OZAK 720 E";

    /// <summary>Urun tipi: bel tipi / tripod turnike (§04.1).</summary>
    public const string ProductType = "Bel tipi tripod turnike";

    /// <summary>Kontrol girisi turu (§04.3).</summary>
    public const string ControlInterface = "Kuru kontak veya TTL/CMOS";

    /// <summary>Kontrol gerilimi alt siniri, volt (§04.3).</summary>
    public const int MinControlVoltage = 5;

    /// <summary>Kontrol gerilimi ust siniri, volt (§04.3).</summary>
    public const int MaxControlVoltage = 48;

    /// <summary>
    /// Role darbe suresi. Uretici dokumaninda BELGELENMEMISTIR; bu deger kurulumda saha
    /// dogrulamasi gerektirir (§08: belgede olmayan detay UNKNOWN kabul edilir).
    /// Varsayilan, kuru kontakli turnikelerde yaygin olan degerdir ve gerektiginde daraltilmalidir.
    /// </summary>
    public static readonly TimeSpan DefaultRelayPulse = TimeSpan.FromMilliseconds(500);

    /// <summary>Role darbe suresi icin kabul edilen alt sinir.</summary>
    public static readonly TimeSpan MinRelayPulse = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Role darbe suresi icin kabul edilen ust sinir. Uzun bir darbe, kontagin turnike bir sonraki
    /// gecise hazir olduktan sonra da kapali kalmasina, yani tek okutmayla birden fazla kisinin
    /// gecmesine ("tailgating") yol acar.
    /// </summary>
    public static readonly TimeSpan MaxRelayPulse = TimeSpan.FromSeconds(5);

    /// <param name="RelayPulse">Kontagin kapali tutulacagi sure.</param>
    /// <param name="SupportsBidirectional">
    /// 720 E uc kolludur ve iki yonde de donebilir; ancak sahadaki mekanik yonlendirme tek yone
    /// kilitlenmis olabilir. Bu yuzden cift yon varsayilmaz, yapilandirmayla bildirilir.
    /// </param>
    public OzakTurnstileProfile(TimeSpan? RelayPulse = null, bool SupportsBidirectional = false)
    {
        var pulse = RelayPulse ?? DefaultRelayPulse;
        if (pulse < MinRelayPulse || pulse > MaxRelayPulse)
        {
            throw new ArgumentOutOfRangeException(nameof(RelayPulse),
                $"Röle darbe süresi {MinRelayPulse.TotalMilliseconds:0}-{MaxRelayPulse.TotalMilliseconds:0} ms arasında olmalıdır.");
        }

        this.RelayPulse = pulse;
        this.SupportsBidirectional = SupportsBidirectional;
    }

    public TimeSpan RelayPulse { get; init; }
    public bool SupportsBidirectional { get; init; }

    /// <summary>
    /// Istenen yonun bu kurulumda fiziksel olarak surulebilir olup olmadigi. Cift yon desteklenmiyorsa
    /// yalnizca giris yonu surulebilir; cikisi "basarili" saymak, hic donmemis bir turnikeyi
    /// acilmis gibi kaydeder.
    /// </summary>
    public bool CanDrive(TurnstileDirection direction) =>
        SupportsBidirectional || direction == TurnstileDirection.Entry;
}
