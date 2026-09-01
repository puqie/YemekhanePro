using System.Text;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Sf300;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// SF300 protokol uygulamasi: cerceveleme, komut/yanit eslesmesi ve hata siniflandirmasi.
/// Gecici hatalar (mesgul, zaman asimi) yeniden denenebilir; kalici hatalar denenmemelidir.
/// </summary>
public sealed class Sf300ProtocolTests
{
    private static readonly DeviceEndpoint Endpoint = new("Ethernet", IpAddress: "10.0.0.5", IpPort: 4370);

    [Fact]
    public async Task HandshakeReturnsDeviceInfoAndCapabilities()
    {
        var transport = new FakeTransport();
        transport.Reply(Sf300Command.Handshake, "SF300|SN-123|1.4.2");
        await using var protocol = new Sf300Protocol(transport);

        await protocol.ConnectAsync(Endpoint, default);
        var info = await protocol.GetDeviceInfoAsync(default);

        Assert.Equal("SF300", info!.Model);
        Assert.Equal("SN-123", info.SerialNumber);
        Assert.Equal("1.4.2", info.Firmware);
        Assert.Contains(DeviceCapability.SendCard, info.Capabilities);
        Assert.Contains(DeviceCapability.GrantAccess, info.Capabilities);
    }

    [Fact]
    public async Task SendCardWritesCardToDeviceAndReportsSuccess()
    {
        var transport = new FakeTransport();
        transport.Reply(Sf300Command.Handshake, "SF300|SN-1|1.0");
        transport.Reply(Sf300Command.SendCard, "OK");
        await using var protocol = new Sf300Protocol(transport);
        await protocol.ConnectAsync(Endpoint, default);

        var result = await protocol.SendCardAsync("0012345678", "student-7", default);

        Assert.True(result!.Succeeded);
        var request = transport.Requests.Single(x => x.Command == Sf300Command.SendCard);
        var payload = Encoding.ASCII.GetString(request.Payload.Span);
        Assert.Equal("0012345678|student-7", payload);
    }

    [Fact]
    public async Task ReadCardReturnsAssignedUserAndNullWhenAbsent()
    {
        var transport = new FakeTransport();
        transport.Reply(Sf300Command.Handshake, "SF300|SN-1|1.0");
        transport.Reply(Sf300Command.ReadCard, "student-7");
        transport.Reply(Sf300Command.ReadCard, "");
        await using var protocol = new Sf300Protocol(transport);
        await protocol.ConnectAsync(Endpoint, default);

        Assert.Equal("student-7", await protocol.ReadCardAsync("0012345678", default));
        Assert.Null(await protocol.ReadCardAsync("0099999999", default));
    }

    [Fact]
    public async Task DeviceBusyResponseIsTransientSoTheCallerMayRetry()
    {
        var transport = new FakeTransport();
        transport.Reply(Sf300Command.Handshake, "SF300|SN-1|1.0");
        transport.ReplyNak(Sf300Command.SendCard, "BUSY");
        await using var protocol = new Sf300Protocol(transport);
        await protocol.ConnectAsync(Endpoint, default);

        var exception = await Assert.ThrowsAsync<Sf300ProtocolException>(
            () => protocol.SendCardAsync("001", "student-1", default));

        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task UnknownCardResponseIsPermanentSoRetryingIsPointless()
    {
        var transport = new FakeTransport();
        transport.Reply(Sf300Command.Handshake, "SF300|SN-1|1.0");
        transport.ReplyNak(Sf300Command.SendCard, "INVALID_CARD");
        await using var protocol = new Sf300Protocol(transport);
        await protocol.ConnectAsync(Endpoint, default);

        var exception = await Assert.ThrowsAsync<Sf300ProtocolException>(
            () => protocol.SendCardAsync("bad", "student-1", default));

        Assert.False(exception.IsTransient);
    }

    [Fact]
    public async Task CorruptedResponseIsRejectedRatherThanTreatedAsSuccess()
    {
        var transport = new FakeTransport();
        transport.Reply(Sf300Command.Handshake, "SF300|SN-1|1.0");
        transport.ReplyRaw(CorruptChecksum(Sf300Frame.Encode(Sf300Command.Ack, "OK"u8.ToArray())));
        await using var protocol = new Sf300Protocol(transport);
        await protocol.ConnectAsync(Endpoint, default);

        await Assert.ThrowsAsync<Sf300ProtocolException>(
            () => protocol.GrantAccessAsync(TurnstileDirection.Entry, default));
    }

    [Fact]
    public async Task MismatchedResponseCodeIsNotReportedAsSuccess()
    {
        // Kanal senkronizasyonu bozuldugunda (gec gelen onceki yanit, kendiliginden gonderilen
        // cerceve) cihaz komutu hic islememis olabilir. "Yanit geldi" ile "komut onaylandi"
        // ayni sey degildir: aksi halde karti hic yazmamis bir turnike "yuklendi" isaretlenir.
        var transport = new FakeTransport();
        transport.Reply(Sf300Command.Handshake, "SF300|SN-1|1.0");
        transport.ReplyRaw(Sf300Frame.Encode(Sf300Command.Status, "READY"u8.ToArray()));
        await using var protocol = new Sf300Protocol(transport);
        await protocol.ConnectAsync(Endpoint, default);

        var exception = await Assert.ThrowsAsync<Sf300ProtocolException>(
            () => protocol.SendCardAsync("0012345678", "student-7", default));

        Assert.Equal("SF300_UNEXPECTED_RESPONSE", exception.ErrorCode);
        Assert.True(exception.IsTransient);
    }

    [Fact]
    public async Task PartialFrameArrivingInPiecesIsReassembled()
    {
        // TCP akis tabanlidir: tek bir cerceve birden fazla pakette gelebilir.
        var transport = new FakeTransport { SplitReads = true };
        transport.Reply(Sf300Command.Handshake, "SF300|SN-1|1.0");
        await using var protocol = new Sf300Protocol(transport);

        await protocol.ConnectAsync(Endpoint, default);
        var info = await protocol.GetDeviceInfoAsync(default);

        Assert.Equal("SF300", info!.Model);
    }

    [Fact]
    public async Task CardEventsArePublishedAsTheyArrive()
    {
        var transport = new FakeTransport();
        transport.Reply(Sf300Command.Handshake, "SF300|SN-1|1.0");
        await using var protocol = new Sf300Protocol(transport);
        await protocol.ConnectAsync(Endpoint, default);
        transport.PushUnsolicited(Sf300Command.CardEvent, "0012345678|Entry");

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var card in protocol.ReadCardsAsync(cancellation.Token))
        {
            Assert.Equal("0012345678", card.CardNumber);
            return;
        }

        Assert.Fail("Kart olayi alinamadi.");
    }

    private static byte[] CorruptChecksum(byte[] frame)
    {
        frame[^2] ^= 0xFF;
        return frame;
    }
}
