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
///
/// <para>
/// <see cref="IAccessController"/> ILAN EDILIR: kart itme dongusu (DeviceCardPushWorker) yalnizca
/// bu arayuzu tasiyan cihazlara kart gonderir. Once ilan edilmiyordu ve SC403 kart kuyrugunda
/// sessizce atlaniyordu -- SDK calissa bile cihaza tek kart gitmeyecekti.
/// </para>
/// </summary>
public sealed class Sc403AccessController : Sc403Adapter, IAccessController
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
    /// <para>
    /// Her basarisizlik BASARISIZ SONUC olarak doner, istisna olarak DEGIL. Fark kritiktir:
    /// <see cref="TurnstileService"/> tuketilen yemek hakkini yalnizca komut basarisiz SONUC
    /// dondurdugunde iade eder (compensateConsumption); atilan istisna genel catch bloguna
    /// duser ve orada iade ISTENMEZ. Role surulmediyse turnike kesinlikle donmemistir, dolayisiyla
    /// hak iade edilmelidir.
    /// </para>
    /// </summary>
    public async Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!_turnstile.CanDrive(direction))
        {
            return new DeviceCommandResult(false,
                $"{OzakTurnstileProfile.Model} turnikesi bu kurulumda {direction} yönünde sürülemiyor.",
                "ZK_DIRECTION_UNSUPPORTED");
        }

        try
        {
            EnsureAvailable(DeviceCapability.GrantAccess);
            return await ExecuteResultAsync(DeviceCapability.GrantAccess,
                    token => RequireSdk().UnlockAsync(_turnstile.RelayPulse, token), "kapı rölesi", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DeviceCapabilityException exception)
        {
            return new DeviceCommandResult(false, exception.Message, "ZK_CAPABILITY");
        }
        catch (DeviceConnectionException exception)
        {
            return new DeviceCommandResult(false,
                $"{OzakTurnstileProfile.Model} turnikesi sürülemedi: {exception.Message}",
                exception.ErrorCode ?? ZkTecoErrorCodes.ProtocolError);
        }
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
}
