using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.CardReaders;
using Yemekhane.Devices.Sf300;
using Yemekhane.Devices.Simulators;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.Devices.ZkTeco;

namespace Yemekhane.Devices.Management;

public sealed record DeviceAdapterConfiguration(
    Guid Id, string Name, string DeviceType, string ConnectionType,
    string? ComPort, int? BaudRate, string? IpAddress, int? IpPort, bool HasTurnstile,
    int? TurnstileRelayPulseMs = null, bool TurnstileBidirectional = false);

public interface IDeviceAdapterFactory
{
    IDevice Create(DeviceAdapterConfiguration configuration);
}

public sealed class DeviceAdapterFactory : IDeviceAdapterFactory
{
    private readonly bool isDevelopment;
    private readonly Func<DeviceAdapterConfiguration, ISf300Protocol?> sf300ProtocolFactory;
    private readonly Func<DeviceAdapterConfiguration, IZkTecoSdk?> zkTecoSdkFactory;

    /// <param name="sf300Protocol">
    /// Tek bir paylasilan protokol ornegi (testler icin). Uretimde her cihaz kendi TCP baglantisini
    /// almalidir; bunun icin fabrika alan diger kurucuyu kullanin.
    /// </param>
    public DeviceAdapterFactory(bool isDevelopment, ISf300Protocol? sf300Protocol = null,
        IZkTecoSdk? zkTecoSdk = null)
        : this(isDevelopment, _ => sf300Protocol, _ => zkTecoSdk) { }

    /// <summary>
    /// Her SF300 cihazi icin ayri bir protokol ornegi uretir. Tek bir ornegi paylasmak,
    /// iki turnikenin ayni TCP soketi uzerinden konusmasina ve yanitlarin karismasina yol acardi.
    /// </summary>
    public DeviceAdapterFactory(bool isDevelopment,
        Func<DeviceAdapterConfiguration, ISf300Protocol?> sf300ProtocolFactory,
        Func<DeviceAdapterConfiguration, IZkTecoSdk?>? zkTecoSdkFactory = null)
    {
        this.isDevelopment = isDevelopment;
        this.sf300ProtocolFactory = sf300ProtocolFactory;
        this.zkTecoSdkFactory = zkTecoSdkFactory ?? (_ => null);
    }

    /// <summary>
    /// Kurulumda girilen turnike ayarlarindan fiziksel profili kurar. Role darbe suresi uretici
    /// dokumaninda belgelenmedigi icin sahada dogrulanir; bu yuzden sabit degil yapilandirilabilir.
    /// </summary>
    private static OzakTurnstileProfile TurnstileProfile(DeviceAdapterConfiguration configuration) =>
        new(configuration.TurnstileRelayPulseMs is { } pulse ? TimeSpan.FromMilliseconds(pulse) : null,
            configuration.TurnstileBidirectional);

    public IDevice Create(DeviceAdapterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = new DeviceEndpoint(configuration.ConnectionType, configuration.ComPort,
            configuration.BaudRate, configuration.IpAddress, configuration.IpPort);

        return configuration.DeviceType switch
        {
            // Gecici hatalar (mesgul, zaman asimi) icin yeniden deneme acik olmalidir; varsayilan 0
            // birakilirsa Sf300ProtocolException.IsTransient siniflandirmasi uretimde hic kullanilmaz
            // ve anlik bir mesguliyet kart yuklemesini kalici basarisiz gosterir.
            "SF300" => new SF300Adapter(configuration.Id, configuration.Name, endpoint,
                sf300ProtocolFactory(configuration), maxRetryCount: 2),
            // SC403 tek basina turnike degildir; kapi rolesine bir turnike bagliysa
            // (HasTurnstile) rolesi suren denetleyici surumu uretilir.
            "SC403" when configuration.HasTurnstile => new Sc403AccessController(configuration.Id,
                configuration.Name, endpoint, TurnstileProfile(configuration),
                zkTecoSdkFactory(configuration), maxRetryCount: 2),
            "SC403" => new Sc403Adapter(configuration.Id, configuration.Name, endpoint,
                zkTecoSdkFactory(configuration), maxRetryCount: 2),
            "ComReader" => new ComCardReader(configuration.Id, configuration.Name, endpoint),
            "EthernetReader" => new EthernetCardReader(configuration.Id, configuration.Name, endpoint),
            "Simulator" when !isDevelopment => throw new InvalidOperationException(
                "Simulator cihazları yalnızca Development ortamında kullanılabilir."),
            "Simulator" when configuration.HasTurnstile =>
                new SimulatorTurnstile(configuration.Id, configuration.Name, endpoint),
            "Simulator" => new SimulatorCardReader(configuration.Id, configuration.Name, endpoint),
            _ => throw new ArgumentException($"Desteklenmeyen cihaz türü: {configuration.DeviceType}",
                nameof(configuration))
        };
    }
}
