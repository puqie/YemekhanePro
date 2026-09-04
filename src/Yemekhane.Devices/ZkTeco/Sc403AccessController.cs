using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Turnstiles;

namespace Yemekhane.Devices.ZkTeco;

/// <summary>
/// Kapi rolesi uzerinden bir turnike suren SC403.
///
/// Sahadaki kurulum sudur: SC403 karti okur, yazilim gecis kararini verir ve karar olumluysa
/// SC403'un kapi rolesi kapatilarak OZAK 720 E turnikesi acilir. Turnike kendi basina bir ag
/// cihazi degildir (bkz. <see cref="OzakTurnstileProfile"/>), bu yuzden <see cref="ITurnstile"/>
/// uygulamasi buraya, yani rolesi surene aittir.
/// </summary>
public sealed class Sc403AccessController : Sc403Adapter, ITurnstile
{
    private readonly OzakTurnstileProfile _turnstile;

    public Sc403AccessController(Guid id, string name, DeviceEndpoint endpoint,
        OzakTurnstileProfile? turnstileProfile = null, IZkTecoSdk? sdk = null,
        TimeSpan? operationTimeout = null, int maxRetryCount = 0)
        : base(id, name, endpoint, sdk, operationTimeout, maxRetryCount) =>
        _turnstile = turnstileProfile ?? new OzakTurnstileProfile();

    /// <summary>Bu denetleyicinin surdugu turnikenin fiziksel profili.</summary>
    public OzakTurnstileProfile TurnstileProfile => _turnstile;

    /// <summary>
    /// Rolesi kapatarak turnikeyi acar.
    ///
    /// Fiziksel olarak surulemeyen bir yon istendiginde komut BASARISIZ dondurulur, atilmaz:
    /// <see cref="TurnstileService"/> basarisiz sonucu tuketilen yemek hakkini iade eden
    /// REVIEW_REQUIRED yoluna sokar. Sessizce basarili saymak, donmemis bir turnikeyi acilmis
    /// gibi kaydeder ve ogrencinin hakkini yakardi.
    /// </summary>
    public Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_turnstile.CanDrive(direction))
        {
            return Task.FromResult(new DeviceCommandResult(false,
                $"{OzakTurnstileProfile.Model} turnikesi bu kurulumda {direction} yönünde sürülemiyor.",
                "ZK_DIRECTION_UNSUPPORTED"));
        }

        EnsureAvailable(DeviceCapability.GrantAccess);
        return Task.FromResult(OpenDoor());
    }

    /// <summary>
    /// Erisim reddi. Kuru kontakli bir turnikede "reddetme" ayri bir komut DEGILDIR: role hic
    /// kapatilmaz ve turnike kilitli kalir. Bu yuzden burada cihaza komut GONDERILMEZ; reddin
    /// dogru fiziksel karsiligi hicbir sey yapmamaktir.
    ///
    /// Sonuc yine de basarili dondurulur: komut amacina ulasmistir (gecis verilmedi).
    /// </summary>
    public Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureAvailable(DeviceCapability.DenyAccess);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new DeviceCommandResult(true,
            $"Geçiş reddedildi; {OzakTurnstileProfile.Model} turnikesi kilitli bırakıldı."));
    }

    /// <summary>
    /// Kapi rolesini darbeleyerek turnikeyi acar.
    ///
    /// SDK dokumaninda (§02.2) listelenen fonksiyonlar arasinda kapi rolesini suren bir cagri
    /// ADI GECMEMEKTEDIR. Dokumanda karsiligi olmayan bir fonksiyon adi uydurmak yasaktir (§08),
    /// bu yuzden gercek surus cihaz basinda dogrulanmis SDK baglamasina birakilir.
    ///
    /// Sonuc ISTISNA ILE DEGIL, BASARISIZ SONUC ILE bildirilir. Fark kritiktir:
    /// <see cref="TurnstileService"/> tuketilen yemek hakkini yalnizca komut basarisiz SONUC
    /// dondurdugunde iade eder (compensateConsumption: isAllowed); atilan istisna ise genel catch
    /// bloguna duser ve orada iade ISTENMEZ. Burada cihaza hicbir komut gonderilmedigi icin
    /// fiziksel sonuc belirsiz de degildir: turnike kesinlikle donmemistir. Dolayisiyla dogru
    /// davranis, yukaridaki yon denetimiyle ayni sekilde basarisiz sonuc dondurmektir.
    /// </summary>
    private static DeviceCommandResult OpenDoor() => new(false,
        $"{OzakTurnstileProfile.Model} turnikesini süren kapı rölesi çağrısı, ZKTeco Standalone SDK " +
        "dokümanında adı geçen fonksiyonlar arasında bulunmamaktadır. Cihaz başında doğrulama gereklidir.",
        ZkTecoErrorCodes.ValidationRequired);
}
