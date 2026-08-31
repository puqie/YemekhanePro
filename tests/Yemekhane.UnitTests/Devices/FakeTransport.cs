using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Yemekhane.Devices.Sf300;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// SF300 protokol testleri icin bellek-ici cift yonlu kanal.
/// Yanitlar komut bazinda kuyruklanir: gercek cihaz da her komuta kendi yanitini dondurur,
/// tek bir global kuyruk kullanmak bir komutun yanitini digerine verirdi.
/// </summary>
internal sealed class FakeTransport : ISf300Transport
{
    private readonly ConcurrentDictionary<Sf300Command, ConcurrentQueue<byte[]>> _scripted = new();
    private readonly ConcurrentQueue<byte[]> _rawReplies = new();
    private readonly Channel<byte> _inbound = Channel.CreateUnbounded<byte>();
    private readonly ConcurrentQueue<Sf300Frame> _requests = new();

    public bool IsConnected { get; private set; }
    public bool SplitReads { get; init; }
    public IReadOnlyList<Sf300Frame> Requests => _requests.ToArray();

    /// <summary>Belirtilen komuta verilecek yaniti kuyruklar.</summary>
    public void Reply(Sf300Command command, string payload) =>
        Queue(command, Sf300Frame.Encode(ResponseFor(command), Encoding.ASCII.GetBytes(payload)));

    /// <summary>Belirtilen komuda NAK dondurur.</summary>
    public void ReplyNak(Sf300Command command, string code) =>
        Queue(command, Sf300Frame.Encode(Sf300Command.Nak, Encoding.ASCII.GetBytes(code)));

    /// <summary>Ham (ornegin bozulmus) bir cerceveyi bir sonraki komuda dondurur.</summary>
    public void ReplyRaw(byte[] frame) => _rawReplies.Enqueue(frame);

    public void PushUnsolicited(Sf300Command command, string payload) =>
        Publish(Sf300Frame.Encode(command, Encoding.ASCII.GetBytes(payload)));

    public Task ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        if (!Sf300Frame.TryDecode(bytes.Span, out var frame, out _) || frame is null) return Task.CompletedTask;
        _requests.Enqueue(frame);

        if (_rawReplies.TryDequeue(out var raw)) { Publish(raw); return Task.CompletedTask; }
        if (_scripted.TryGetValue(frame.Command, out var queue) && queue.TryDequeue(out var reply)) Publish(reply);
        return Task.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var first = await _inbound.Reader.ReadAsync(cancellationToken);
        buffer.Span[0] = first;
        var count = 1;
        // SplitReads: her cagride tek bayt dondurerek parcali TCP okumasini taklit eder.
        if (!SplitReads)
            while (count < buffer.Length && _inbound.Reader.TryRead(out var next)) buffer.Span[count++] = next;
        return count;
    }

    public ValueTask DisposeAsync()
    {
        IsConnected = false;
        _inbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void Queue(Sf300Command command, byte[] frame) =>
        _scripted.GetOrAdd(command, _ => new ConcurrentQueue<byte[]>()).Enqueue(frame);

    private void Publish(byte[] frame)
    {
        foreach (var value in frame) _inbound.Writer.TryWrite(value);
    }

    private static Sf300Command ResponseFor(Sf300Command command) => command switch
    {
        Sf300Command.Handshake => Sf300Command.Handshake,
        Sf300Command.ReadCard => Sf300Command.ReadCard,
        Sf300Command.ReadUser => Sf300Command.ReadUser,
        Sf300Command.Status => Sf300Command.Status,
        _ => Sf300Command.Ack
    };
}
