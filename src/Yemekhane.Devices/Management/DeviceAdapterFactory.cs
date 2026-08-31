using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.CardReaders;
using Yemekhane.Devices.Sf300;
using Yemekhane.Devices.Simulators;

namespace Yemekhane.Devices.Management;

public sealed record DeviceAdapterConfiguration(
    Guid Id, string Name, string DeviceType, string ConnectionType,
    string? ComPort, int? BaudRate, string? IpAddress, int? IpPort, bool HasTurnstile);

public interface IDeviceAdapterFactory
{
    IDevice Create(DeviceAdapterConfiguration configuration);
}

public sealed class DeviceAdapterFactory : IDeviceAdapterFactory
{
    private readonly bool isDevelopment;
    private readonly Func<DeviceAdapterConfiguration, ISf300Protocol?> sf300ProtocolFactory;

    /// <param name="sf300Protocol">
    /// Tek bir paylasilan protokol ornegi (testler icin). Uretimde her cihaz kendi TCP baglantisini
    /// almalidir; bunun icin fabrika alan diger kurucuyu kullanin.
    /// </param>
    public DeviceAdapterFactory(bool isDevelopment, ISf300Protocol? sf300Protocol = null)
        : this(isDevelopment, _ => sf300Protocol) { }

    /// <summary>
    /// Her SF300 cihazi icin ayri bir protokol ornegi uretir. Tek bir ornegi paylasmak,
    /// iki turnikenin ayni TCP soketi uzerinden konusmasina ve yanitlarin karismasina yol acardi.
    /// </summary>
    public DeviceAdapterFactory(bool isDevelopment,
        Func<DeviceAdapterConfiguration, ISf300Protocol?> sf300ProtocolFactory)
    {
        this.isDevelopment = isDevelopment;
        this.sf300ProtocolFactory = sf300ProtocolFactory;
    }

    public IDevice Create(DeviceAdapterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var endpoint = new DeviceEndpoint(configuration.ConnectionType, configuration.ComPort,
            configuration.BaudRate, configuration.IpAddress, configuration.IpPort);

        return configuration.DeviceType switch
        {
            "SF300" => new SF300Adapter(configuration.Id, configuration.Name, endpoint,
                sf300ProtocolFactory(configuration)),
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
