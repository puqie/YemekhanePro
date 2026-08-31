using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yemekhane.Api.Devices;
using Yemekhane.Application.Devices;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Devices;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Kart yukleme isciisinin hata davranisi: gecici hatalar yeniden denenir, kalici hatalar
/// denenmez ve bir cihazin cevrimdisi olmasi diger cihazlara yuklemeyi durdurmaz.
/// </summary>
[Collection(Persistence.LocalDatabaseTests.CollectionName)]
public sealed class DeviceCardPushWorkerTests
{
    [Fact]
    public async Task CardsArePushedToConnectedDeviceAndMarkedLoaded()
    {
        await using var harness = await Harness.CreateAsync();
        var device = harness.AddDevice("Ana Giriş");
        var card = await harness.AddCardAsync();
        await harness.Sync.QueueCardAsync(card.Id, default);

        await harness.RunOnceAsync();

        Assert.Contains(device.Sent, x => x.CardNumber == card.CardNumber);
        var status = await harness.Sync.GetCardStatusAsync(card.Id, default);
        Assert.Equal(DeviceCardSyncStatus.Loaded, status.Single().Status);
    }

    [Fact]
    public async Task PermanentDeviceErrorStopsRetryingThatCard()
    {
        await using var harness = await Harness.CreateAsync();
        var device = harness.AddDevice("Ana Giriş");
        device.FailWith = new DeviceConnectionException("Ana Giriş", "gecersiz", "SF300_INVALID_CARD");
        var card = await harness.AddCardAsync();
        await harness.Sync.QueueCardAsync(card.Id, default);

        await harness.RunOnceAsync();

        var status = await harness.Sync.GetCardStatusAsync(card.Id, default);
        Assert.Equal(DeviceCardSyncStatus.Failed, status.Single().Status);
        Assert.Empty(await harness.Sync.GetPendingAsync(device.Id, 10, default));
    }

    [Fact]
    public async Task TransientDeviceErrorLeavesCardPendingForTheNextRound()
    {
        await using var harness = await Harness.CreateAsync();
        var device = harness.AddDevice("Ana Giriş");
        device.FailWith = new DeviceConnectionException("Ana Giriş", "mesgul", "SF300_BUSY");
        var card = await harness.AddCardAsync();
        await harness.Sync.QueueCardAsync(card.Id, default);

        await harness.RunOnceAsync();

        Assert.Single(await harness.Sync.GetPendingAsync(device.Id, 10, default));

        // Cihaz duzeldiginde ayni kart bir sonraki turda yuklenir.
        device.FailWith = null;
        await harness.RunOnceAsync();
        var status = await harness.Sync.GetCardStatusAsync(card.Id, default);
        Assert.Equal(DeviceCardSyncStatus.Loaded, status.Single().Status);
    }

    [Fact]
    public async Task OfflineDeviceDoesNotBlockCardsGoingToHealthyDevices()
    {
        await using var harness = await Harness.CreateAsync();
        var offline = harness.AddDevice("Arka Kapı");
        offline.State = DeviceConnectionState.Disconnected;
        var healthy = harness.AddDevice("Ana Giriş");
        var card = await harness.AddCardAsync();
        await harness.Sync.QueueCardAsync(card.Id, default);

        await harness.RunOnceAsync();

        Assert.Contains(healthy.Sent, x => x.CardNumber == card.CardNumber);
        var rows = await harness.Sync.GetCardStatusAsync(card.Id, default);
        Assert.Equal(DeviceCardSyncStatus.Loaded, rows.Single(x => x.DeviceId == healthy.Id).Status);
        Assert.Equal(DeviceCardSyncStatus.Pending, rows.Single(x => x.DeviceId == offline.Id).Status);
    }

    [Fact]
    public async Task RepeatedTransientFailuresEventuallyBecomePermanent()
    {
        // Sinirsiz yeniden deneme, arizali bir kartin kuyrugu surekli mesgul etmesine yol acardi.
        await using var harness = await Harness.CreateAsync(maxAttempts: 3);
        var device = harness.AddDevice("Ana Giriş");
        device.FailWith = new DeviceConnectionException("Ana Giriş", "mesgul", "SF300_BUSY");
        var card = await harness.AddCardAsync();
        await harness.Sync.QueueCardAsync(card.Id, default);

        for (var round = 0; round < 3; round++) await harness.RunOnceAsync();

        var status = await harness.Sync.GetCardStatusAsync(card.Id, default);
        Assert.Equal(DeviceCardSyncStatus.Failed, status.Single().Status);
        Assert.Empty(await harness.Sync.GetPendingAsync(device.Id, 10, default));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private static int nextAddress;
        private readonly SqliteConnection connection;
        private readonly ServiceProvider provider;
        private readonly DeviceCardPushWorker worker;
        private int cardCounter;

        private Harness(SqliteConnection connection, ServiceProvider provider, YemekhaneDbContext context,
            DeviceRegistry registry, DeviceCardPushWorker worker)
        {
            this.connection = connection;
            this.provider = provider;
            Context = context;
            Registry = registry;
            this.worker = worker;
            Sync = new DeviceCardSyncService(context, TimeProvider.System);
        }

        public YemekhaneDbContext Context { get; }
        public DeviceRegistry Registry { get; }
        public DeviceCardSyncService Sync { get; }

        public static async Task<Harness> CreateAsync(int maxAttempts = 10)
        {
            var connection = new SqliteConnection($"Data Source=push-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options;
            var context = new YemekhaneDbContext(options);
            await context.Database.MigrateAsync();

            var registry = new DeviceRegistry();
            var services = new ServiceCollection();
            services.AddScoped<IDeviceCardSyncService>(_ => new DeviceCardSyncService(context, TimeProvider.System));
            var provider = services.BuildServiceProvider();
            var worker = new DeviceCardPushWorker(provider.GetRequiredService<IServiceScopeFactory>(), registry,
                TimeProvider.System, new DeviceCardPushOptions { MaxAttempts = maxAttempts },
                NullLogger<DeviceCardPushWorker>.Instance);
            return new Harness(connection, provider, context, registry, worker);
        }

        public FakeController AddDevice(string name)
        {
            var address = Interlocked.Increment(ref nextAddress);
            var entity = new Device
            {
                Name = name, DeviceType = "SF300", ConnectionType = "Ethernet", Direction = "Entry",
                ConnectionStatus = "Connected", IsActive = true,
                IpAddress = $"10.1.{address / 250}.{address % 250 + 1}", IpPort = 4370
            };
            Context.Add(entity);
            Context.SaveChanges();
            var controller = new FakeController(entity.Id, name);
            Registry.Register(controller);
            return controller;
        }

        public async Task<StudentCard> AddCardAsync()
        {
            var index = Interlocked.Increment(ref cardCounter);
            var student = new Student { StudentNo = Guid.NewGuid().ToString("N"), FirstName = "Ayşe", LastName = "Yılmaz" };
            var card = new StudentCard
            {
                StudentId = student.Id, CardNumber = $"card-{index:D6}", ValidFrom = DateTimeOffset.UtcNow
            };
            Context.AddRange(student, card);
            await Context.SaveChangesAsync();
            return card;
        }

        public Task RunOnceAsync() => worker.PushPendingCardsAsync(default);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await provider.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    private sealed class FakeController(Guid id, string name) : IAccessController
    {
        private readonly List<(string CardNumber, string ExternalId)> sent = [];

        public Guid Id { get; } = id;
        public string Name { get; } = name;
        public DeviceEndpoint Endpoint { get; } = new("Ethernet", IpAddress: "10.0.0.1", IpPort: 4370);
        public DeviceConnectionState State { get; set; } = DeviceConnectionState.Connected;
        public DeviceConnectionState ConnectionState => State;
        public DeviceConnectionException? FailWith { get; set; }
        public IReadOnlyList<(string CardNumber, string ExternalId)> Sent => sent;
        public IReadOnlySet<DeviceCapability> Capabilities { get; } =
            new HashSet<DeviceCapability>(Enum.GetValues<DeviceCapability>());

        public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceInfo("SF300", null, null, Capabilities));
        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceStatus(State, DateTimeOffset.UtcNow));

        public Task<DeviceCommandResult> SendCardAsync(string cardNumber, string externalUserId,
            CancellationToken cancellationToken)
        {
            if (FailWith is not null) throw FailWith;
            sent.Add((cardNumber, externalUserId));
            return Task.FromResult(new DeviceCommandResult(true, "OK"));
        }

        public Task<DeviceCommandResult> SyncCardAsync(string cardNumber, string externalUserId,
            CancellationToken cancellationToken) => SendCardAsync(cardNumber, externalUserId, cancellationToken);
        public Task<DeviceCommandResult> SendUserAsync(DeviceUser user, CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceCommandResult(true, "OK"));
        public Task<DeviceCommandResult> SyncUserAsync(DeviceUser user, CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceCommandResult(true, "OK"));
        public Task<DeviceUser?> ReadUserAsync(string externalUserId, CancellationToken cancellationToken) =>
            Task.FromResult<DeviceUser?>(null);
        public Task<string?> ReadCardAsync(string cardNumber, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
        public Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceCommandResult(true, "OK"));
        public Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceCommandResult(true, "OK"));
        public async IAsyncEnumerable<CardReadEvent> ReadCardsAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
