using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yemekhane.Application.Common;

namespace Yemekhane.Sync;

public static class SyncRegistration
{
    public static IServiceCollection AddYemekhaneSync(
        this IServiceCollection services,
        Uri serverBaseAddress,
        SyncEngineOptions? engineOptions = null,
        SyncWorkerOptions? workerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(serverBaseAddress);
        OutboundEndpointPolicy.ValidateSyntax(serverBaseAddress.ToString());

        services.AddSingleton(engineOptions ?? new SyncEngineOptions());
        services.AddSingleton(workerOptions ?? new SyncWorkerOptions());
        services.TryAddSingleton<IConnectivityMonitor, NetworkConnectivityMonitor>();
        services.AddHttpClient<ISyncTransport, HttpSyncTransport>(client => client.BaseAddress = serverBaseAddress)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddScoped<SyncEngine>();
        services.AddHostedService<SyncBackgroundWorker>();
        return services;
    }
}
