using Yemekhane.Licensing;

namespace Yemekhane.UnitTests.Licensing;

public sealed class FingerprintMatcherTests
{
    private static string[] Machine(string board, string disk, string guid) =>
        [FingerprintHasher.Hash(board), FingerprintHasher.Hash(disk), FingerprintHasher.Hash(guid)];

    [Fact]
    public void AnIdenticalMachineMatches()
    {
        var stored = Machine("ANAKART-1", "DISK-1", "GUID-1");

        Assert.True(FingerprintMatcher.Matches(stored, Machine("ANAKART-1", "DISK-1", "GUID-1")));
    }

    [Fact]
    public void ReplacingOneComponentStillMatches()
    {
        // Musteri diskini degistirdiginde lisansi gecersizlesmemelidir; aksi halde her
        // donanim bakiminda destek cagrisi gelir.
        var stored = Machine("ANAKART-1", "DISK-1", "GUID-1");

        Assert.True(FingerprintMatcher.Matches(stored, Machine("ANAKART-1", "YENI-DISK", "GUID-1")));
    }

    [Fact]
    public void ReplacingTwoComponentsDoesNotMatch()
    {
        // Iki bilesen birden degistiyse bu artik baska bir makinedir: lisansin
        // ikinci bir bilgisayara kopyalanmasi tam olarak boyle gorunur.
        var stored = Machine("ANAKART-1", "DISK-1", "GUID-1");

        Assert.False(FingerprintMatcher.Matches(stored, Machine("ANAKART-2", "DISK-2", "GUID-1")));
    }

    [Fact]
    public void UnreadableComponentsOnBothSidesDoNotCountAsAMatch()
    {
        // WMI erisimi kisitli iki AYRI makinede bilesenler bos okunur. Bos degerleri
        // esit saymak, bu makineleri birbirinin ayni gosterip lisansi serbestce
        // kopyalanabilir hale getirirdi.
        var stored = new[] { string.Empty, string.Empty, FingerprintHasher.Hash("GUID-1") };
        var other = new[] { string.Empty, string.Empty, FingerprintHasher.Hash("GUID-2") };

        Assert.Equal(0, FingerprintMatcher.CountMatches(stored, other));
        Assert.False(FingerprintMatcher.Matches(stored, other));
    }

    [Fact]
    public void HashingIsCaseAndWhitespaceInsensitive()
    {
        // WMI ayni seri numarasini surumden surume farkli bicimlendirebilir; bu fark
        // lisansi gecersiz kilmamalidir.
        Assert.Equal(FingerprintHasher.Hash("abc-123"), FingerprintHasher.Hash("  ABC-123 "));
    }

    [Fact]
    public void AnUnreadableComponentHashesToEmptySoItIsNotConfusedWithAValue()
    {
        Assert.Equal(string.Empty, FingerprintHasher.Hash(null));
        Assert.Equal(string.Empty, FingerprintHasher.Hash("   "));
    }

    [Fact]
    public void AFingerprintWithNoReadableComponentsIsUnusable()
    {
        // Uc bilesenin de okunamadigi makinede aktivasyon REDDEDILMELIDIR; sessizce
        // "her makine gecerli" durumuna dusmek lisansi tamamen anlamsiz kilardi.
        var blind = new HardwareFingerprint([string.Empty, string.Empty, string.Empty]);

        Assert.False(blind.IsUsable);
        Assert.Equal("BILINMIYOR", blind.MachineId);
    }

    [Fact]
    public void TheMachineIdIsStableAndShortEnoughToReadOverThePhone()
    {
        // Destek, kullanicidan bu kimligi telefonda okumasini ister.
        var fingerprint = new HardwareFingerprint(Machine("ANAKART-1", "DISK-1", "GUID-1"));

        Assert.Equal(12, fingerprint.MachineId.Length);
        Assert.Equal(fingerprint.MachineId, new HardwareFingerprint(Machine("ANAKART-1", "DISK-1", "GUID-1")).MachineId);
    }
}
