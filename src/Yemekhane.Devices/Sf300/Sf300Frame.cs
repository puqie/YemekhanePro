namespace Yemekhane.Devices.Sf300;

/// <summary>SF300 komut kodlari.</summary>
public enum Sf300Command : byte
{
    Handshake = 0x01,
    Status = 0x02,
    ReadCard = 0x20,
    ReadUser = 0x21,
    SendCard = 0x10,
    SendUser = 0x11,
    DeleteCard = 0x12,
    GrantAccess = 0x30,
    DenyAccess = 0x31,
    CardEvent = 0x40,
    Ack = 0x06,
    Nak = 0x15
}

/// <summary>
/// SF300 cerceve bicimi: STX | LEN | CMD | DATA... | XOR | ETX
/// LEN, CMD ve DATA baytlarinin toplam sayisidir. XOR saglamasi LEN, CMD ve DATA uzerinden hesaplanir;
/// yalnizca veriyi kapsasaydi komut baytindaki bir bozulma fark edilmezdi.
/// </summary>
public sealed class Sf300Frame
{
    public const byte Stx = 0x02;
    public const byte Etx = 0x03;

    /// <summary>LEN tek bayt oldugundan LEN + CMD + DATA en fazla 255 olabilir.</summary>
    public const int MaxPayloadLength = 253;

    private Sf300Frame(Sf300Command command, ReadOnlyMemory<byte> payload)
    {
        Command = command;
        Payload = payload;
    }

    public Sf300Command Command { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public static byte[] Encode(Sf300Command command, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"SF300 veri alani en fazla {MaxPayloadLength} bayt olabilir.");
        }

        var length = (byte)(payload.Length + 2);
        var frame = new byte[payload.Length + 5];
        frame[0] = Stx;
        frame[1] = length;
        frame[2] = (byte)command;
        payload.CopyTo(frame.AsSpan(3));
        frame[^2] = Checksum(frame.AsSpan(1, payload.Length + 2));
        frame[^1] = Etx;
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> bytes, out Sf300Frame? frame, out string? error)
    {
        frame = null;
        if (bytes.Length < 5)
        {
            error = "SF300 cercevesi eksik.";
            return false;
        }

        if (bytes[0] != Stx || bytes[^1] != Etx)
        {
            error = "SF300 cerceve sinirlayicilari (STX/ETX) gecersiz.";
            return false;
        }

        var length = bytes[1];
        if (length < 2 || length + 3 != bytes.Length)
        {
            error = "SF300 cerceve uzunlugu bildirilen degerle uyusmuyor.";
            return false;
        }

        var expected = Checksum(bytes.Slice(1, length));
        if (expected != bytes[^2])
        {
            error = "SF300 cerceve saglama toplami dogrulanamadi.";
            return false;
        }

        if (!Enum.IsDefined(typeof(Sf300Command), bytes[2]))
        {
            error = $"SF300 bilinmeyen komut kodu: 0x{bytes[2]:X2}.";
            return false;
        }

        frame = new Sf300Frame((Sf300Command)bytes[2], bytes.Slice(3, length - 2).ToArray());
        error = null;
        return true;
    }

    private static byte Checksum(ReadOnlySpan<byte> bytes)
    {
        byte checksum = 0;
        foreach (var value in bytes) checksum ^= value;
        return checksum;
    }
}
