using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Yemekhane.Api.Devices;
using Yemekhane.Application.Common;
using Yemekhane.Devices.Management;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.Devices.ZkTeco;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Audit;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Kurulumu yapan sube, cihazin baglanti ve turnike surus ayarlarini kendisi girip
/// duzenleyebilmelidir. Role darbe suresi ve yon kilidi uretici dokumaninda belgelenmedigi icin
/// sahada dogrulanir; sabit varsayilanla birakilirsa yanlis turnike davranisi duzeltilemez.
/// </summary>
public sealed class TurnstileDriveSettingsTests
{
    private static DeviceWriteRequest Request(bool hasTurnstile = true, int? pulse = 250,
        bool bidirectional = true, string ip = "10.0.0.50", int? cycle = 5) =>
        new("SC403 Giris", "SC403", "Ethernet", ip, 4370, null, null,
            IsActive: true, AutoConnect: false, HasTurnstile: hasTurnstile,
            Location: "Yemekhane", Direction: "Entry",
            TurnstileRelayPulseMs: pulse, TurnstileBidirectional: bidirectional, TurnstileCycleSeconds: cycle);

    /// <summary>Dongu suresi 0-60 sn disina cikamaz: cok uzun deger her okutmayi bekletir.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(61)]
    public async Task CycleOutsideTheAllowedRangeIsRejected(int cycle)
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Service.CreateAsync(Request(cycle: cycle), default));
    }

    /// <summary>Turnike isareti kaldirilinca dongu suresi de temizlenir; sonradan geri gelmez.</summary>
    [Fact]
    public async Task CycleIsClearedWhenTheTurnstileIsUnchecked()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(Request(cycle: 9), default);

        var updated = await fixture.Service.UpdateAsync(created.Id, Request(hasTurnstile: false, pulse: null, cycle: 9), default);

        Assert.Null(updated.TurnstileCycleSeconds);
    }
    /// <summary>Sube girdigi degerler kaydedilmeli ve geri okunabilmelidir.</summary>
    [Fact]
    public async Task BranchCanEnterAndReadBackTurnstileSettings()
    {
        await using var fixture = await Fixture.CreateAsync();

        var created = await fixture.Service.CreateAsync(Request(), default);

        Assert.Equal(250, created.TurnstileRelayPulseMs);
        Assert.Equal(5, created.TurnstileCycleSeconds);
        Assert.True(created.TurnstileBidirectional);
        Assert.Equal("10.0.0.50", created.IpAddress);
        Assert.Equal(4370, created.Port);
    }

    /// <summary>Kurulumdan sonra ayarlar duzenlenebilmelidir.</summary>
    [Fact]
    public async Task BranchCanEditSettingsAfterInstallation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(Request(pulse: 250, bidirectional: true), default);

        var updated = await fixture.Service.UpdateAsync(created.Id,
            Request(pulse: 900, bidirectional: false, ip: "10.0.0.77", cycle: 7), default);

        Assert.Equal(900, updated.TurnstileRelayPulseMs);
        // Turnike dongusu de duzenlenebilir: saha olcumu 5 sn degil 7 sn cikarsa ekrandan degistirilir.
        Assert.Equal(7, updated.TurnstileCycleSeconds);
        Assert.False(updated.TurnstileBidirectional);
        Assert.Equal("10.0.0.77", updated.IpAddress);
    }

    /// <summary>
    /// Asiri uzun darbe, kontagin turnike bir sonraki gecise hazir olduktan sonra da kapali
    /// kalmasina — tek okutmayla birden fazla kisinin gecmesine — yol acar.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(9000)]
    public async Task OutOfRangeRelayPulseIsRefused(int pulse)
    {
        await using var fixture = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<RequestValidationException>(
            () => fixture.Service.CreateAsync(Request(pulse: pulse), default));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(5000)]
    public async Task RelayPulseBoundsAreAccepted(int pulse)
    {
        await using var fixture = await Fixture.CreateAsync();

        var created = await fixture.Service.CreateAsync(Request(pulse: pulse), default);

        Assert.Equal(pulse, created.TurnstileRelayPulseMs);
    }

    /// <summary>
    /// Turnike bagli degilken girilen turnike ayarlari saklanmamalidir; aksi halde cihaz sonradan
    /// turnikeye baglandiginda eski bir deger sessizce geri gelir.
    /// </summary>
    [Fact]
    public async Task TurnstileSettingsAreClearedWhenNoTurnstileAttached()
    {
        await using var fixture = await Fixture.CreateAsync();

        var created = await fixture.Service.CreateAsync(
            Request(hasTurnstile: false, pulse: 250, bidirectional: true), default);

        Assert.Null(created.TurnstileRelayPulseMs);
        Assert.False(created.TurnstileBidirectional);
    }

    /// <summary>
    /// Kritik: girilen ayar gercekten donanim adaptorune ULASMALIDIR. Yalnizca veritabanina
    /// yazilip fabrikada sabit varsayilan kullanilirsa sube ayari degistirir ama turnike
    /// davranisi hic degismez.
    /// </summary>
    [Fact]
    public async Task EnteredSettingsReachTheHardwareAdapter()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(Request(pulse: 750, bidirectional: true), default);
        var entity = await fixture.Db.Devices.AsNoTracking().SingleAsync(x => x.Id == created.Id);

        var factory = new DeviceAdapterFactory(isDevelopment: false);
        await using var device = factory.Create(Adapter(entity));

        var controller = Assert.IsType<Sc403AccessController>(device);
        Assert.Equal(TimeSpan.FromMilliseconds(750), controller.TurnstileProfile.RelayPulse);
        Assert.True(controller.TurnstileProfile.SupportsBidirectional);
        Assert.True(controller.TurnstileProfile.CanDrive(TurnstileDirection.Exit));
    }

    /// <summary>Deger girilmediyse belgelenmis varsayilan kullanilir.</summary>
    [Fact]
    public async Task DefaultProfileIsUsedWhenPulseNotEntered()
    {
        await using var fixture = await Fixture.CreateAsync();
        var created = await fixture.Service.CreateAsync(Request(pulse: null, bidirectional: false), default);
        var entity = await fixture.Db.Devices.AsNoTracking().SingleAsync(x => x.Id == created.Id);

        var factory = new DeviceAdapterFactory(isDevelopment: false);
        await using var device = factory.Create(Adapter(entity));

        var controller = Assert.IsType<Sc403AccessController>(device);
        Assert.Equal(OzakTurnstileProfile.DefaultRelayPulse, controller.TurnstileProfile.RelayPulse);
        Assert.False(controller.TurnstileProfile.CanDrive(TurnstileDirection.Exit));
    }

    /// <summary>Ayni IP ve port baska bir cihazda kullanilamaz.</summary>
    [Fact]
    public async Task DuplicateEndpointIsRefused()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.CreateAsync(Request(ip: "10.0.0.60"), default);

        await Assert.ThrowsAsync<RequestValidationException>(() => fixture.Service.CreateAsync(
            Request(ip: "10.0.0.60") with { Name = "SC403 Cikis" }, default));
    }

    /// <summary>
    /// Kayitli cihazdan adaptor yapilandirmasini kurar. DeviceAdministrationService.Configuration
    /// internal oldugundan ayni donusum burada yinelenir; alan eslesmesi bozulursa testler duser.
    /// </summary>
    private static DeviceAdapterConfiguration Adapter(Yemekhane.Domain.Entities.Device x) =>
        new(x.Id, x.Name, x.DeviceType, x.ConnectionType, x.ComPort, x.BaudRate, x.IpAddress,
            x.IpPort, x.HasTurnstile, x.TurnstileRelayPulseMs, x.TurnstileBidirectional);

    private sealed class Fixture : IAsyncDisposable
    {
        private SqliteConnection connection = null!;
        public YemekhaneDbContext Db { get; private set; } = null!;
        public DeviceAdministrationService Service { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.connection = new SqliteConnection(
                $"Data Source=file:turnstile-settings-{Guid.NewGuid():N}?mode=memory&cache=shared");
            await fixture.connection.OpenAsync();
            var options = new DbContextOptionsBuilder<YemekhaneDbContext>()
                .UseSqlite(fixture.connection).Options;
            fixture.Db = new YemekhaneDbContext(options);
            await fixture.Db.Database.MigrateAsync();
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));
            fixture.Service = new DeviceAdministrationService(fixture.Db, new DeviceManager(),
                new DeviceAdapterFactory(isDevelopment: true),
                new AuditService(new EfAuditRepository(fixture.Db, clock), new SystemAuditContext()),
                clock, new StubEnvironment());
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
