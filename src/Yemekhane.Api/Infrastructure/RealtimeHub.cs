using Microsoft.AspNetCore.SignalR;
using Yemekhane.Application.Realtime;

namespace Yemekhane.Api.Infrastructure;

public interface IRealtimeClient
{
    Task AccessDecisionCommitted(AccessDecisionCommittedEvent realtimeEvent);
    Task TurnstileResult(TurnstileResultEvent realtimeEvent);
    Task DeviceStatusChanged(DeviceStatusChangedEvent realtimeEvent);
    Task Notification(NotificationEvent realtimeEvent);
}

public sealed class RealtimeHub : Hub<IRealtimeClient>
{
    public override async Task OnConnectedAsync()
    {
        var userId = Context.UserIdentifier;
        if (!string.IsNullOrWhiteSpace(userId)) await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(userId));
        foreach (var permission in Context.User?.FindAll("permission").Select(x => x.Value).Distinct(StringComparer.Ordinal) ?? [])
            await Groups.AddToGroupAsync(Context.ConnectionId, PermissionGroup(permission));
        await base.OnConnectedAsync();
    }

    internal static string UserGroup(string userId) => $"notifications:user:{userId}";
    internal static string PermissionGroup(string permission) => $"notifications:permission:{permission}";

    public Task Subscribe(string channel)
    {
        ValidateChannel(channel);
        return Groups.AddToGroupAsync(Context.ConnectionId, channel);
    }

    public Task Unsubscribe(string channel)
    {
        ValidateChannel(channel);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, channel);
    }

    private static void ValidateChannel(string channel)
    {
        if (!RealtimeChannels.All.Contains(channel))
        {
            throw new HubException($"Bilinmeyen real-time kanalı: {channel}");
        }
    }
}
