using System.Buffers.Binary;

namespace Yemekhane.Devices.ZkTeco.Protocol;

/// <summary>
/// Iletisim sifresi (comm key) el sikisma degeri. Cihaz CONNECT'e ACK_UNAUTH ile cevap verirse
/// AUTH komutuyla bu deger gonderilir.
///
/// pyzk <c>__make_commkey</c> ile birebir; iki farkli girdi vektorunde Python ile bayt bayt
/// dogrulandi. Algoritma: anahtarin 32 bitini tersine cevir, oturum kimligini ekle, 'Z','K','S','O'
/// ile XOR'la, 16-bit yarilari takas et, tick degeriyle XOR'la (ucuncu bayt tick'in kendisi).
/// </summary>
public static class ZkCommKey
{
    public static byte[] Create(int key, int sessionId, int ticks = 50)
    {
        uint reversed = 0;
        for (var bit = 0; bit < 32; bit++)
        {
            reversed = (key & (1 << bit)) != 0 ? (reversed << 1) | 1 : reversed << 1;
        }

        // pyzk burada Python'un sinirsiz tam sayisiyla toplar ve pack('I') ile 32 bite indirger;
        // unchecked toplama ayni sonucu verir.
        var value = unchecked(reversed + (uint)sessionId);
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        bytes[0] ^= (byte)'Z';
        bytes[1] ^= (byte)'K';
        bytes[2] ^= (byte)'S';
        bytes[3] ^= (byte)'O';

        var tick = (byte)(ticks & 0xFF);
        return
        [
            (byte)(bytes[2] ^ tick),
            (byte)(bytes[3] ^ tick),
            tick,
            (byte)(bytes[1] ^ tick),
        ];
    }
}
