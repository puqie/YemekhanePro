namespace Yemekhane.Api.Infrastructure;

public sealed class ParentProcessLifetimeService(IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var command = await Console.In.ReadLineAsync(stoppingToken).ConfigureAwait(false);
            if (command is null || string.Equals(command, "shutdown", StringComparison.OrdinalIgnoreCase))
                lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
