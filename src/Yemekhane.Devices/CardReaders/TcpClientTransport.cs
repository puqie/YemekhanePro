using System.Net.Sockets;

namespace Yemekhane.Devices.CardReaders;

internal sealed class TcpClientTransport(string host, int port) : ITcpTransport
{
    private TcpClient? _client;
    private bool _disposed;

    public bool IsConnected
    {
        get
        {
            var socket = _client?.Client;
            if (_disposed || socket is null || !socket.Connected)
            {
                return false;
            }

            try
            {
                return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch (SocketException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
        {
            return;
        }

        _client?.Dispose();
        var client = new TcpClient { NoDelay = true };
        _client = client;
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            if (ReferenceEquals(Interlocked.CompareExchange(ref _client, null, client), client))
            {
                // The failed client was removed. A concurrent disconnect may already have done this.
            }

            throw;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var client = _client;
        if (client is null || !IsConnected)
        {
            return ValueTask.FromResult(0);
        }

        return client.GetStream().ReadAsync(buffer, cancellationToken);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _client, null)?.Dispose();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        Interlocked.Exchange(ref _client, null)?.Dispose();
        return ValueTask.CompletedTask;
    }
}
