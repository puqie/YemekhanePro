using System.IO.Ports;

namespace Yemekhane.Devices.CardReaders;

internal sealed class SerialPortTransport : ISerialTransport
{
    private readonly SerialPort _serialPort;
    private bool _disposed;

    public SerialPortTransport(string portName, int baudRate)
    {
        _serialPort = new SerialPort(portName, baudRate)
        {
            Encoding = System.Text.Encoding.ASCII,
            Handshake = Handshake.None
        };
    }

    public bool IsOpen => !_disposed && _serialPort.IsOpen;

    public Task OpenAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _serialPort.Open();
        return Task.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _serialPort.BaseStream.ReadAsync(buffer, cancellationToken);
    }

    public Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_disposed && _serialPort.IsOpen)
        {
            _serialPort.Close();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _serialPort.Dispose();
        return ValueTask.CompletedTask;
    }
}
