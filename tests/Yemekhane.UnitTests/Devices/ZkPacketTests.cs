using Yemekhane.Devices.ZkTeco;
using Yemekhane.Devices.ZkTeco.Protocol;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// ZK paket bicimi. Beklenen baytlar pyzk referans uygulamasindan Python ile uretildi ve
/// PowerShell sondasiyla capraz dogrulandi; burada sabitlenir ki gelecekte "duzeltilmis"
/// bir checksum sessizce referanstan sapmasin -- cihaz o zaman cevap vermez ve suc cihaza
/// atilir (bir kez oldu).
/// </summary>
public sealed class ZkPacketTests
{
    /// <summary>
    /// CONNECT paketi, session=0, reply=65534: checksum 0xFC17 (65534 uzerinden), pakete
    /// (65534+1) % 65535 = 0 yazilir. Python referansi: E8 03 17 FC 00 00 00 00.
    /// </summary>
    [Fact]
    public void ConnectPacketMatchesReferenceBytes()
    {
        var packet = ZkPacket.Build(ZkCommands.Connect, sessionId: 0, replyId: 65534, data: []);

        Assert.Equal(new byte[] { 0xE8, 0x03, 0x17, 0xFC, 0x00, 0x00, 0x00, 0x00 }, packet);
    }

    [Fact]
    public void ChecksumMatchesReference()
    {
        var checksum = ZkPacket.ComputeChecksum([0xE8, 0x03, 0x00, 0x00, 0x00, 0x00, 0xFE, 0xFF]);

        Assert.Equal(0xFC17, checksum);
    }

    /// <summary>Tek kalan bayt kelime olarak degil, oldugu gibi toplanir (referans davranis).</summary>
    [Fact]
    public void ChecksumHandlesOddLength()
    {
        // 0x0001 + 0x0002 + 0x03 = 6 -> 65535 - 6 - 1 = 65528
        Assert.Equal(65528, ZkPacket.ComputeChecksum([0x01, 0x00, 0x02, 0x00, 0x03]));
    }

    /// <summary>Referans tuhafligi: paketteki reply_id, checksum'da kullanilanin bir fazlasidir.</summary>
    [Fact]
    public void BuildWritesNextReplyIdIntoHeader()
    {
        var packet = ZkPacket.Build(ZkCommands.Exit, sessionId: 0x1234, replyId: 10, data: [1, 2, 3]);
        var parsed = ZkPacket.Parse(packet);

        Assert.Equal(ZkCommands.Exit, parsed.Command);
        Assert.Equal(0x1234, parsed.SessionId);
        Assert.Equal(11, parsed.ReplyId);
        Assert.Equal(new byte[] { 1, 2, 3 }, parsed.Data);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(65533, 65534)]
    [InlineData(65534, 0)]
    public void ReplyIdWrapsAtUShortMax(int current, int expected) =>
        Assert.Equal(expected, ZkPacket.NextReplyId((ushort)current));

    [Fact]
    public void ParseRejectsShortDatagram()
    {
        var exception = Assert.Throws<ZkTecoProtocolException>(() => ZkPacket.Parse([0xE8, 0x03, 0x17]));

        Assert.Equal(ZkTecoErrorCodes.InvalidResponse, exception.ErrorCode);
        Assert.False(exception.IsTransient);
    }

    [Fact]
    public void ParseReadsAckWithEmptyData()
    {
        var parsed = ZkPacket.Parse([0xD0, 0x07, 0x00, 0x00, 0x39, 0x30, 0x01, 0x00]);

        Assert.Equal(ZkCommands.AckOk, parsed.Command);
        Assert.Equal(12345, parsed.SessionId);
        Assert.True(parsed.IsAck);
        Assert.False(parsed.IsEvent);
        Assert.Empty(parsed.Data);
    }
}

/// <summary>Comm key vektorleri pyzk <c>__make_commkey</c> ile Python'da uretildi.</summary>
public sealed class ZkCommKeyTests
{
    [Theory]
    [InlineData(0, 12345, new byte[] { 0x61, 0x7D, 0x32, 0x49 })]
    [InlineData(123456, 4321, new byte[] { 0x26, 0x7F, 0x32, 0xE9 })]
    public void MatchesReferenceVectors(int key, int session, byte[] expected) =>
        Assert.Equal(expected, ZkCommKey.Create(key, session));

    /// <summary>Ucuncu bayt her zaman tick'in kendisidir; cihaz bunu okuyup diger baytlari cozer.</summary>
    [Fact]
    public void ThirdByteIsTick() =>
        Assert.Equal(0x32, ZkCommKey.Create(999, 1, ticks: 50)[2]);
}
