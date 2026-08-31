using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Yemekhane.Sync;

public interface IConnectivityMonitor
{
    bool IsOnline { get; }
    event Action? ConnectivityRestored;
}

public sealed class SyncWorkerOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(1);
}

public sealed class SyncBackgroundWorker(
    IServiceScopeFactory scopeFactory,
    IConnectivityMonitor connectivity,
    SyncWorkerOptions? options = null) : BackgroundService
{
    private readonly SyncWorkerOptions _options = options ?? new SyncWorkerOptions();
    private readonly Channel<bool> _connectivitySignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_options.Interval, TimeSpan.Zero);
        connectivity.ConnectivityRestored += OnConnectivityRestored;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (connectivity.IsOnline)
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var engine = scope.ServiceProvider.GetRequiredService<SyncEngine>();
                    await engine.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }

                using var triggerCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var periodic = Task.Delay(_options.Interval, triggerCancellation.Token);
                var restored = _connectivitySignals.Reader.ReadAsync(triggerCancellation.Token).AsTask();
                await Task.WhenAny(periodic, restored).ConfigureAwait(false);
                await triggerCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            connectivity.ConnectivityRestored -= OnConnectivityRestored;
        }
    }

    private void OnConnectivityRestored() => _connectivitySignals.Writer.TryWrite(true);
}
