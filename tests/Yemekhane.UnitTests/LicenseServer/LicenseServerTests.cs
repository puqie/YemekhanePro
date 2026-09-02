using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Licensing;
using Yemekhane.LicenseServer.Data;
using Yemekhane.LicenseServer.Services;

namespace Yemekhane.UnitTests.LicenseServer;

/// <summary>
/// Lisans sunucusunun is mantigi.
///
/// En kritik sinama, sunucunun URETTIGI imzanin masaustundeki dogrulayicidan GECMESIDIR:
/// ikisi ayni koddan (LicenseSignature) beslenir, ama alan sirasi ya da IssuedAt secimi
/// tutmazsa sunucu lisansi satar, musteri kurar ve uygulama "lisans kurcalanmis" der.
/// Bu hata ancak sahada gorunurdu.
/// </summary>
public sealed class LicenseServerTests : IDisposable
{
    private const string Secret = "test-imza-sirri-en-az-32-bayt-olmali-123456";
    private static readonly string[] Fingerprints = ["AAAA1111", "BBBB2222", "CCCC3333"];

    private readonly SqliteConnection connection;
    private readonly LicenseDbContext db;
    private readonly FixedClock clock = new(new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));

    public LicenseServerTests()
    {
        connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        db = new LicenseDbContext(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); }

    private LicenseServerService Server => new(db, clock, Secret);
    private LicenseAdminService Admin => new(db, clock);

    /// <summary>
    /// Sunucunun imzaladigi lisans, masaustundeki dogrulayicidan GECMELI.
    /// Bu test iki tarafin ayni imzayi hesapladigini kanitlar.
    /// </summary>
    [Fact]
    public async Task SunucununImzasiMasaustundeDogrulanir()
    {
        var created = await Admin.CreateAsync("Şehit Öğretmen Lisesi", "Standart", null, null, default);
        var reply = await Server.ActivateAsync(created.LicenseKey, Fingerprints, default);

        Assert.Equal(ActivateOutcome.Activated, reply.Outcome);
        var stored = new StoredLicense(created.LicenseKey, reply.License!.CustomerName, reply.License.Edition,
            Fingerprints, reply.License.ActivatedAt!.Value, reply.License.ExpiresAt,
            reply.License.ActivatedAt!.Value, reply.Signature!);

        Assert.True(LicenseSignature.Verify(stored, Secret),
            "Sunucunun ürettiği imza masaüstünde doğrulanamadı: sahada 'lisans kurcalanmış' hatası verirdi.");
    }

    /// <summary>Yanlis sirla imzalanmis lisans REDDEDILMELI; aksi halde imza koruma saglamaz.</summary>
    [Fact]
    public async Task FarkliSirlaImzalanmisLisansReddedilir()
    {
        var created = await Admin.CreateAsync("Deneme Okulu", "Standart", null, null, default);
        var reply = await new LicenseServerService(db, clock, "baska-bir-sir-en-az-32-bayt-olmali-99").
            ActivateAsync(created.LicenseKey, Fingerprints, default);

        var stored = new StoredLicense(created.LicenseKey, "Deneme Okulu", "Standart", Fingerprints,
            clock.GetUtcNow(), null, clock.GetUtcNow(), reply.Signature!);

        Assert.False(LicenseSignature.Verify(stored, Secret));
    }

    [Fact]
    public async Task BilinmeyenAnahtarBulunamadiDoner()
    {
        var reply = await Server.ActivateAsync("YMK-2026-XXXX-YYYY", Fingerprints, default);
        Assert.Equal(ActivateOutcome.NotFound, reply.Outcome);
    }

    /// <summary>Lisans TEK makineye baglanir: ikinci bir makine 409 almalidir.</summary>
    [Fact]
    public async Task BaskaMakineAktivasyonuReddedilir()
    {
        var created = await Admin.CreateAsync("Okul", "Standart", null, null, default);
        await Server.ActivateAsync(created.LicenseKey, Fingerprints, default);

        var other = await Server.ActivateAsync(created.LicenseKey, ["ZZZZ9999", "YYYY8888", "XXXX7777"], default);
        Assert.Equal(ActivateOutcome.AlreadyBound, other.Outcome);
    }

    /// <summary>
    /// AYNI makine tekrar aktive edilebilmeli: musteri bilgisayarini formatlarsa ya da
    /// uygulamayi yeniden kurarsa destege mahkum kalmamali.
    /// </summary>
    [Fact]
    public async Task AyniMakineYenidenAktiveEdilebilir()
    {
        var created = await Admin.CreateAsync("Okul", "Standart", null, null, default);
        await Server.ActivateAsync(created.LicenseKey, Fingerprints, default);

        var again = await Server.ActivateAsync(created.LicenseKey, Fingerprints, default);
        Assert.Equal(ActivateOutcome.Activated, again.Outcome);
    }

    [Fact]
    public async Task IptalEdilmisLisansAktiveEdilemezVeDogrulamadaDuser()
    {
        var created = await Admin.CreateAsync("Okul", "Standart", null, null, default);
        await Server.ActivateAsync(created.LicenseKey, Fingerprints, default);
        Assert.True(await Admin.RevokeAsync(created.LicenseKey, "Ödeme yapılmadı", default));

        Assert.Equal(ActivateOutcome.Revoked, (await Server.ActivateAsync(created.LicenseKey, Fingerprints, default)).Outcome);
        var validation = await Server.ValidateAsync(created.LicenseKey, Fingerprints, default);
        Assert.True(validation.Revoked);
    }

    /// <summary>
    /// Veritabanindan SILINMIS (ya da hic olmayan) anahtar da iptal sayilmali; aksi halde
    /// kaydi silinen bir lisans sahada sonsuza kadar calismaya devam ederdi.
    /// </summary>
    [Fact]
    public async Task BilinmeyenAnahtarDogrulamadaIptalSayilir()
    {
        var validation = await Server.ValidateAsync("YMK-2026-YOKK-YOKK", Fingerprints, default);
        Assert.True(validation.Revoked);
    }

    /// <summary>Suresi dolmus lisans yenilenmeden aktive edilememeli.</summary>
    [Fact]
    public async Task SuresiDolmusLisansAktiveEdilemez()
    {
        var created = await Admin.CreateAsync("Okul", "Standart", 1, null, default);
        clock.Advance(TimeSpan.FromDays(400));

        Assert.Equal(ActivateOutcome.Expired, (await Server.ActivateAsync(created.LicenseKey, Fingerprints, default)).Outcome);
    }

    /// <summary>Suresiz lisansta bitis tarihi YOKTUR; yillik lisansta tam bir yil sonradir.</summary>
    [Fact]
    public async Task SuresizVeYillikLisanslarDogruTarihAlir()
    {
        var perpetual = await Admin.CreateAsync("Okul A", "Kurumsal", null, null, default);
        var annual = await Admin.CreateAsync("Okul B", "Standart", 1, null, default);

        Assert.Null(perpetual.ExpiresAt);
        Assert.True(perpetual.IsPerpetual);
        Assert.Equal(clock.GetUtcNow().AddYears(1), annual.ExpiresAt);
    }

    /// <summary>
    /// Erken yenileyen musteri gun KAYBETMEMELI: uzatma mevcut bitisin uzerine eklenir.
    /// Suresi dolmussa bugunden baslar, yoksa gecmise uzatma yapilirdi.
    /// </summary>
    [Fact]
    public async Task UzatmaGecerliLisansaEklenirDolmusaBugundenBaslar()
    {
        var early = await Admin.CreateAsync("Okul A", "Standart", 1, null, default);
        var expected = early.ExpiresAt!.Value.AddYears(1);
        var extended = await Admin.ExtendAsync(early.LicenseKey, 1, default);
        Assert.Equal(expected, extended!.ExpiresAt);

        var late = await Admin.CreateAsync("Okul B", "Standart", 1, null, default);
        clock.Advance(TimeSpan.FromDays(400));
        var renewed = await Admin.ExtendAsync(late.LicenseKey, 1, default);
        Assert.Equal(clock.GetUtcNow().AddYears(1), renewed!.ExpiresAt);
    }

    /// <summary>Makine cozuldukten sonra lisans YENI bir bilgisayarda aktive edilebilmeli.</summary>
    [Fact]
    public async Task MakineCozulunceYeniBilgisayardaAktiveEdilir()
    {
        var created = await Admin.CreateAsync("Okul", "Standart", null, null, default);
        await Server.ActivateAsync(created.LicenseKey, Fingerprints, default);
        Assert.True(await Admin.ReleaseMachineAsync(created.LicenseKey, default));

        var moved = await Server.ActivateAsync(created.LicenseKey, ["NEW11111", "NEW22222", "NEW33333"], default);
        Assert.Equal(ActivateOutcome.Activated, moved.Outcome);
    }

    /// <summary>Anahtar bosluklu ve kucuk harfle yazilsa da ayni lisansi bulmali.</summary>
    [Theory]
    [InlineData("ymk-2026-aaaa-bbbb")]
    [InlineData("  YMK-2026-AAAA-BBBB  ")]
    public async Task AnahtarBuyukKucukHarfVeBoslukFarkiniYokSayar(string typed)
    {
        db.Licenses.Add(new LicenseRecord
        {
            LicenseKey = "YMK-2026-AAAA-BBBB", CustomerName = "Okul", Edition = "Standart",
            CreatedAt = clock.GetUtcNow()
        });
        await db.SaveChangesAsync();

        var reply = await Server.ActivateAsync(typed, Fingerprints, default);
        Assert.Equal(ActivateOutcome.Activated, reply.Outcome);
    }

    /// <summary>Uretilen anahtarlar benzersiz ve karisan karakterlerden arindirilmis olmali.</summary>
    [Fact]
    public void UretilenAnahtarlarBenzersizVeOkunakli()
    {
        var keys = Enumerable.Range(0, 500).Select(_ => LicenseKeyGenerator.Create(clock.GetUtcNow())).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        // 0/O ve 1/I/L telefonda okunurken karisir; alfabeye alinmadilar.
        Assert.All(keys, key => Assert.DoesNotContain(key.AsSpan(8).ToString(), c => c is '0' or 'O' or '1' or 'I' or 'L'));
    }

    [Fact]
    public async Task GecersizMusteriAdiVeYilSayisiReddedilir()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Admin.CreateAsync("A", "Standart", null, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Admin.CreateAsync("Okul", "Standart", 0, null, default));
        await Assert.ThrowsAsync<ArgumentException>(() => Admin.CreateAsync("Okul", "Standart", 99, null, default));
    }

    /// <summary>Dogrulama sayaci artmali: lisansin sahada gercekten kullanildigi gorulebilmeli.</summary>
    [Fact]
    public async Task DogrulamaSayaciArtar()
    {
        var created = await Admin.CreateAsync("Okul", "Standart", null, null, default);
        await Server.ActivateAsync(created.LicenseKey, Fingerprints, default);
        await Server.ValidateAsync(created.LicenseKey, Fingerprints, default);
        await Server.ValidateAsync(created.LicenseKey, Fingerprints, default);

        var record = await db.Licenses.AsNoTracking().SingleAsync(x => x.LicenseKey == created.LicenseKey);
        Assert.Equal(2, record.ValidationCount);
        Assert.NotNull(record.LastValidatedAt);
    }

    /// <summary>
    /// Listeleme GERCEKTEN calismali ve en yeni lisans basta gelmeli.
    ///
    /// Ilk surumde siralama sunucuda (OrderByDescending -> SQL) yapiliyordu; SQLite
    /// ORDER BY icinde DateTimeOffset'i CEVIREMEZ ve uc 500 donuyordu. Birim testleri
    /// servisi cagirmadigi surece bu gorunmez: yonetim ekrani hic acilmazdi.
    /// </summary>
    [Fact]
    public async Task ListelemeCalisirVeEnYeniBastaGelir()
    {
        await Admin.CreateAsync("Eski Okul", "Standart", null, null, default);
        clock.Advance(TimeSpan.FromDays(2));
        var newest = await Admin.CreateAsync("Yeni Okul", "Standart", 1, null, default);

        var all = await Admin.ListAsync(null, default);
        Assert.Equal(2, all.Count);
        Assert.Equal(newest.LicenseKey, all[0].LicenseKey);

        var filtered = await Admin.ListAsync("Yeni", default);
        Assert.Single(filtered);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan by) => current = current.Add(by);
    }
}
