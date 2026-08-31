using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Application.Realtime;

namespace Yemekhane.Api.Infrastructure;

public static class RealtimeServiceExtensions
{
    public const string HubPath = "/hubs/realtime";

    public static IServiceCollection AddYemekhaneRealtime(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<RealtimeEventPublisher>();
        services.AddSingleton<IRealtimeEventPublisher>(provider =>
            provider.GetRequiredService<RealtimeEventPublisher>());
        services.AddHostedService(provider => provider.GetRequiredService<RealtimeEventPublisher>());
        services.AddHostedService<DeviceStatusRealtimeBridge>();
        return services;
    }

    public static HubEndpointConventionBuilder MapYemekhaneRealtime(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHub<RealtimeHub>(HubPath);
}
