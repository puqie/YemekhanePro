using System.Net.NetworkInformation;

namespace Yemekhane.Sync;

public sealed class NetworkConnectivityMonitor : IConnectivityMonitor, IDisposable
{
    public NetworkConnectivityMonitor() => NetworkChange.NetworkAvailabilityChanged += OnAvailabilityChanged;

    public bool IsOnline => NetworkInterface.GetIsNetworkAvailable();
    public event Action? ConnectivityRestored;

    public void Dispose() => NetworkChange.NetworkAvailabilityChanged -= OnAvailabilityChanged;

    private void OnAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs eventArgs)
    {
        if (eventArgs.IsAvailable) ConnectivityRestored?.Invoke();
    }
}
