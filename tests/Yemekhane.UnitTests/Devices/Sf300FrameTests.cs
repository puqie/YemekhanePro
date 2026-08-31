using Yemekhane.Devices.Sf300;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// SF300 cerceve kodlayicisi: STX | LEN | CMD | DATA | XOR | ETX.
/// Cihazdan gelen bozuk veya eksik cerceveler sessizce kabul edilmemelidir;
/// hatali bir cerceveyi gecerli saymak yanlis ogrenciye kapi acabilir.
/// </summary>
public sealed class Sf300FrameTests
{
    [Fact]
    public void EncodeProducesFramedPayloadWithChecksum()
    {
        var frame = Sf300Frame.Encode(Sf300Command.Handshake, "ABC"u8.ToArray());

        Assert.Equal(Sf300Frame.Stx, frame[0]);
        Assert.Equal(Sf300Frame.Etx, frame[^1]);
        // LEN = CMD (1) + DATA (3) + LEN alaninin kendisi (1) = 5
        Assert.Equal(5, frame[1]);
        Assert.Equal((byte)Sf300Command.Handshake, frame[2]);
        Assert.Equal((byte)'A', frame[3]);
    }

    [Fact]
    public void DecodeReturnsCommandAndPayloadForValidFrame()
    {
        var encoded = Sf300Frame.Encode(Sf300Command.ReadCard, "12345"u8.ToArray());

        Assert.True(Sf300Frame.TryDecode(encoded, out var frame, out var error));
        Assert.Null(error);
        Assert.Equal(Sf300Command.ReadCard, frame!.Command);
        Assert.Equal("12345", System.Text.Encoding.ASCII.GetString(frame.Payload.Span));
    }

    [Fact]
    public void DecodeRejectsCorruptedChecksum()
    {
        var encoded = Sf300Frame.Encode(Sf300Command.ReadCard, "12345"u8.ToArray());
        encoded[^2] ^= 0xFF;

        Assert.False(Sf300Frame.TryDecode(encoded, out _, out var error));
        Assert.Contains("saglama", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecodeRejectsPayloadThatDoesNotMatchDeclaredLength()
    {
        var encoded = Sf300Frame.Encode(Sf300Command.ReadCard, "12345"u8.ToArray());
        encoded[1] = 99;

        Assert.False(Sf300Frame.TryDecode(encoded, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x02 })]
    [InlineData(new byte[] { 0x99, 0x02, 0x01, 0x00, 0x03 })]
    public void DecodeRejectsMalformedFrames(byte[] bytes)
    {
        Assert.False(Sf300Frame.TryDecode(bytes, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void EncodeRejectsPayloadLargerThanFrameAllows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Sf300Frame.Encode(Sf300Command.SendCard, new byte[Sf300Frame.MaxPayloadLength + 1]));
    }

    [Fact]
    public void ChecksumCoversLengthCommandAndPayload()
    {
        // Yalnizca veriyi kapsayan bir saglama, komut baytindaki bozulmayi kaciririr:
        // "kart yaz" komutu "gecis izni" olarak okunabilirdi.
        var first = Sf300Frame.Encode(Sf300Command.GrantAccess, "X"u8.ToArray());
        var second = Sf300Frame.Encode(Sf300Command.DenyAccess, "X"u8.ToArray());

        Assert.NotEqual(first[^2], second[^2]);
    }
}
