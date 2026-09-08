using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Yemekhane.Api.Devices;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;
using Yemekhane.Devices.Turnstiles;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// Turnikeye bagli okuyuculari dinleyen isci. Bu isci yokken TurnstileService'in hicbir uretim
/// cagirani yoktu: kart okunsa bile olay kimseye ulasmiyor, turnike acilmiyordu.
/// </summary>
public sealed class TurnstileCardReadWorkerTests
{
    [Fact]
    public async Task ConnectedTurnstileReaderIsDrivenAndSwipesReachTheHandler()
    {
        using var harness = new Harness();
        var device = harness.AddTurnstileReader("Yemekhane Girişi");

        harness.Worker.EnsureReaders(harness.Token);
        Assert.Contains(device.Id, harness.Worker.ActiveReaders);

        await device.SwipeAsync("8247129");

        await WaitUntilAsync(() => harness.Handler.Handled.Count == 1);
        Assert.Equal((device.Id, "8247129"), harness.Handler.Handled.Single());
    }

    /// <summary>Turnikesiz okuyucu (kart atama masasi) gecis boru hattina BAGLANMAZ.</summary>
    [Fact]
    public void ReaderWithoutTurnstileIsNotDriven()
    {
        using var harness = new Harness();
        var device = harness.AddReaderOnly("Kayıt Masası");

        harness.Worker.EnsureReaders(harness.Token);

        Assert.DoesNotContain(device.Id, harness.Worker.ActiveReaders);
        Assert.Equal(0, device.Streams);
    }

    [Fact]
    public void DisconnectedDeviceIsNotDriven()
    {
        using var harness = new Harness();
        var device = harness.AddTurnstileReader("Yemekhane Girişi");
        device.ConnectionState = DeviceConnectionState.Faulted;

        harness.Worker.EnsureReaders(harness.Token);

        Assert.DoesNotContain(device.Id, harness.Worker.ActiveReaders);
    }

    /// <summary>Bir kartin islenmesindeki hata akisi bitirmez; sonraki okutma islenir.</summary>
    [Fact]
    public async Task HandlerFailureDoesNotStopTheStream()
    {
        using var harness = new Harness();
        var device = harness.AddTurnstileReader("Yemekhane Girişi");
        harness.Handler.ThrowOn = card => card == "bozuk";
        harness.Worker.EnsureReaders(harness.Token);

        await device.SwipeAsync("bozuk");
        await device.SwipeAsync("8247129");

        await WaitUntilAsync(() => harness.Handler.Handled.Count == 2);
        Assert.Contains(device.Id, harness.Worker.ActiveReaders);
        Assert.Equal("8247129", harness.Handler.Handled.Last().Card);
    }

    /// <summary>
    /// Akis koparsa dongu biter; hemen degil, bekleme suresi gecince yeniden baslatilir (aksi
    /// halde "bagli" gorunen ama okumayan cihazda her tikte hata gunlugu olusurdu).
    /// </summary>
    [Fact]
    public async Task BrokenStreamIsRestartedAfterTheDelay()
    {
        using var harness = new Harness();
        var device = harness.AddTurnstileReader("Yemekhane Girişi");
        harness.Worker.EnsureReaders(harness.Token);
        await WaitUntilAsync(() => device.Streams == 1);

        device.BreakStream();
        await WaitUntilAsync(() => harness.Worker.ActiveReaders.Count == 0);

        harness.Worker.EnsureReaders(harness.Token);
        Assert.Empty(harness.Worker.ActiveReaders);

        harness.Clock.Advance(TimeSpan.FromSeconds(11));
        harness.Worker.EnsureReaders(harness.Token);
        Assert.Contains(device.Id, harness.Worker.ActiveReaders);
        await WaitUntilAsync(() => device.Streams == 2);

        await device.SwipeAsync("5555");
        await WaitUntilAsync(() => harness.Handler.Handled.Count == 1);
    }

    /// <summary>Iki turnike, iki dongu: birinin akisi digerini etkilemez.</summary>
    [Fact]
    public async Task EachTurnstileGetsItsOwnLoop()
    {
        using var harness = new Harness();
        var entry = harness.AddTurnstileReader("Giriş");
        var exit = harness.AddTurnstileReader("Çıkış");
        harness.Worker.EnsureReaders(harness.Token);

        entry.BreakStream();
        await exit.SwipeAsync("1");

        await WaitUntilAsync(() => harness.Handler.Handled.Count == 1);
        Assert.Equal(exit.Id, harness.Handler.Handled.Single().DeviceId);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "Beklenen kosul zamaninda olusmadi.");
    }

    private sealed class Harness : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly ServiceProvider _provider;

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddScoped<ITurnstileCardHandler>(_ => Handler);
            _provider = services.BuildServiceProvider();
            Worker = new TurnstileCardReadWorker(_provider.GetRequiredService<IServiceScopeFactory>(), Registry, Clock,
                new TurnstileReaderOptions { RestartDelaySeconds = 10 }, NullLogger<TurnstileCardReadWorker>.Instance);
        }

        public DeviceRegistry Registry { get; } = new();
        public RecordingHandler Handler { get; } = new();
        public ManualClock Clock { get; } = new(new DateTimeOffset(2026, 9, 8, 9, 20, 0, TimeSpan.FromHours(3)));
        public TurnstileCardReadWorker Worker { get; }
        public CancellationToken Token => _cancellation.Token;

        public FakeTurnstileReader AddTurnstileReader(string name)
        {
            var device = new FakeTurnstileReader(name);
            Registry.Register(device);
            return device;
        }

        public FakeReader AddReaderOnly(string name)
        {
            var device = new FakeReader(name);
            Registry.Register(device);
            return device;
        }

        public void Dispose()
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
            _provider.Dispose();
        }
    }

    private sealed class RecordingHandler : ITurnstileCardHandler
    {
        private readonly List<(Guid DeviceId, string Card)> _handled = [];

        public IReadOnlyList<(Guid DeviceId, string Card)> Handled
        {
            get { lock (_handled) return _handled.ToArray(); }
        }

        public Func<string, bool>? ThrowOn { get; set; }

        public Task<TurnstileResult?> HandleAsync(Guid deviceId, CardReadEvent card, CancellationToken cancellationToken)
        {
            lock (_handled) _handled.Add((deviceId, card.CardNumber));
            if (ThrowOn?.Invoke(card.CardNumber) == true) throw new InvalidOperationException("Kart islenemedi.");
            return Task.FromResult<TurnstileResult?>(null);
        }
    }

    private class FakeReader(string name) : ICardReader
    {
        private Channel<CardReadEvent> _channel = Channel.CreateUnbounded<CardReadEvent>();

        public Guid Id { get; } = Guid.NewGuid();
        public string Name { get; } = name;
        public DeviceEndpoint Endpoint { get; } = new("Ethernet", IpAddress: "169.254.198.201", IpPort: 4370);
        public DeviceConnectionState ConnectionState { get; set; } = DeviceConnectionState.Connected;
        public int Streams { get; private set; }

        public Task<DeviceInfo> ConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceInfo("SC403", null, null, new HashSet<DeviceCapability>()));

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceStatus(ConnectionState, DateTimeOffset.UtcNow));

        public async IAsyncEnumerable<CardReadEvent> ReadCardsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Streams++;
            var reader = _channel.Reader;
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                while (reader.TryRead(out var card)) yield return card;
            }
        }

        public ValueTask SwipeAsync(string cardNumber) =>
            _channel.Writer.WriteAsync(new CardReadEvent(cardNumber, DateTimeOffset.UtcNow, "SC403"));

        /// <summary>Suren akisi hatayla bitirir; sonraki ReadCardsAsync yeni bir akis acar.</summary>
        public void BreakStream()
        {
            _channel.Writer.Complete(new InvalidOperationException("SC403 kart okuma islemi basarisiz: baglanti koptu."));
            _channel = Channel.CreateUnbounded<CardReadEvent>();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTurnstileReader(string name) : FakeReader(name), ITurnstile
    {
        public Task<DeviceCommandResult> GrantAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceCommandResult(true, "acildi"));

        public Task<DeviceCommandResult> DenyAccessAsync(TurnstileDirection direction, CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceCommandResult(true, "reddedildi"));
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public void Advance(TimeSpan by) => _now += by;
        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
    }
}
