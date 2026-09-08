using System.Buffers.Binary;

namespace Yemekhane.Devices.ZkTeco.Protocol;

/// <summary>
/// ZK tel protokolu paketi: <c>cmd(2) checksum(2) session(2) reply(2) data</c>, hepsi little-endian.
///
/// <para>
/// REFERANS TUHAFLIGI BIREBIR KOPYALANIR: pyzk ve node-zklib checksum'i ESKI reply_id ile
/// hesaplar, pakete ise reply_id+1 yazar. Bu, paketin kendi baytlariyla tutarsiz bir checksum
/// demektir; yine de binlerce cihazda calisan davranis budur. "Duzeltmek" (checksum'i gercek
/// baytlar uzerinden hesaplamak) referansla ayni sonucu vermez ve cihazin bunu kabul edip
/// etmeyecegi bilinmez. Cihaz basinda dogrulanmis davranis kopyalanir.
/// </para>
/// </summary>
public readonly record struct ZkPacket(ushort Command, ushort Checksum, ushort SessionId, ushort ReplyId, byte[] Data)
{
    public const int HeaderLength = 8;

    /// <summary>pyzk USHRT_MAX: reply_id bu degere ulasinca sifira sarar.</summary>
    public const int ReplyIdModulus = 65535;

    /// <summary>Bir sonraki reply_id (referans: <c>(reply + 1) % USHRT_MAX</c>).</summary>
    public static ushort NextReplyId(ushort replyId) => (ushort)((replyId + 1) % ReplyIdModulus);

    /// <summary>
    /// node-zklib <c>createChkSum</c> ile birebir; pyzk <c>__create_checksum</c> ile ayni sonucu
    /// verir (Python ile bayt bayt dogrulandi). 16-bit LE kelimeler toplanir, 65535'e gore
    /// indirgenir, tek kalan bayt oldugu gibi eklenir, sonuc <c>65535 - toplam - 1</c>.
    /// </summary>
    public static ushort ComputeChecksum(ReadOnlySpan<byte> packet)
    {
        var sum = 0;
        for (var i = 0; i < packet.Length; i += 2)
        {
            sum += i == packet.Length - 1
                ? packet[i]
                : packet[i] | (packet[i + 1] << 8);
            sum %= ReplyIdModulus;
        }

        return (ushort)(ReplyIdModulus - sum - 1);
    }

    /// <summary>
    /// Gonderilecek paketi kurar. Checksum <paramref name="replyId"/> ile hesaplanir, pakete
    /// <c>NextReplyId(replyId)</c> yazilir (referans tuhafligi, bkz. sinif aciklamasi).
    /// </summary>
    public static byte[] Build(ushort command, ushort sessionId, ushort replyId, ReadOnlySpan<byte> data)
    {
        var buffer = new byte[HeaderLength + data.Length];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, command);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], sessionId);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], replyId);
        data.CopyTo(span[HeaderLength..]);

        var checksum = ComputeChecksum(span);
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], checksum);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], NextReplyId(replyId));
        return buffer;
    }

    /// <summary>
    /// Gelen datagrami cozer. Sekiz bayttan kisa bir datagram gecerli bir paket olamaz;
    /// bunu sessizce bos paket saymak, bozuk bir yaniti "basarili" gostermeye kadar giderdi.
    /// </summary>
    public static ZkPacket Parse(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < HeaderLength)
        {
            throw new ZkTecoProtocolException(
                $"ZK paketi en az {HeaderLength} bayt olmalidir; {datagram.Length} bayt geldi.",
                isTransient: false, ZkTecoErrorCodes.InvalidResponse);
        }

        return new ZkPacket(
            BinaryPrimitives.ReadUInt16LittleEndian(datagram),
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(datagram[6..]),
            datagram[HeaderLength..].ToArray());
    }

    public bool IsAck => Command is ZkCommands.AckOk or ZkCommands.AckData;
    public bool IsEvent => Command == ZkCommands.RegisterEvent;
}
