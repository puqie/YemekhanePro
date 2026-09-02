using System.Text.Json;
using System.Text.RegularExpressions;
using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Ogrenci detay sekmelerinin ham veritabani dokumu yerine okunabilir Turkce
/// satir urettigini dogrular.
///
/// Bu testler gercek bir arayuz hatasindan dogdu: eski bicimlendirici JSON'un
/// ILK ALTI alanini ham adlariyla basiyordu. Odeme kaydinda ilk alti alanin
/// ucu GUID (id, operationId, studentId) oldugu icin TUTAR, gelir turu ve
/// aciklama satira hic giremiyordu -- kullanici odeme gecmisinde odenen
/// parayi goremezken uc tane anlamsiz GUID goruyordu.
///
/// Ornek JSON govdeleri API sozlesmelerinden birebir alinmistir
/// (IncomeTransactionDetails, DailyTrackingRow, CardDetails ...); alan adlari
/// ASP.NET Core'un varsayilan camelCase bicimindedir.
/// </summary>
public sealed class StudentTabFormatterTests
{
    private static string Summarize(string tab, string json)
    {
        using var document = JsonDocument.Parse(json);
        return StudentTabFormatter.Summarize(tab, document.RootElement);
    }

    // IncomeTransactionDetails: alan sirasi kasten sozlesmedeki sirayla,
    // yani tutar 9. sirada -- eski Take(6) tam da burada kesiyordu.
    private const string PaymentJson = """
    {
      "id": "d29e1c49-4b1a-4f0e-9c3d-2b7a5e6f1a8c",
      "operationId": "7f3b2a10-9c4d-4e5f-8a1b-6d2c3e4f5a6b",
      "studentId": "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
      "studentName": "ELİF ÇETİN",
      "cardNumber": "8350001",
      "transactionAt": "2026-09-02T13:00:00+03:00",
      "incomeTypeId": "9b8a7c6d-5e4f-4a3b-2c1d-0e9f8a7b6c5d",
      "incomeTypeName": "Günlük Yemek",
      "amount": 750.00,
      "description": "Eylül ayı ödemesi",
      "createdBy": "3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f",
      "isVoided": false,
      "voidedAt": null,
      "voidedBy": null,
      "voidReason": null
    }
    """;

    /// <summary>
    /// HATANIN OZU: tutar gorunur olmali. Eski bicimlendiricide bu satir
    /// "id: ... | operationId: ... | studentId: ..." ile dolup tutari yutuyordu.
    /// </summary>
    [Fact]
    public void OdemeSatiriTutariGosterir()
    {
        var text = Summarize("Payments", PaymentJson);
        Assert.Contains("750", text, StringComparison.Ordinal);
        Assert.Contains("Tutar: ₺750,00", text, StringComparison.Ordinal);
    }

    /// <summary>Kesilen diger anlamli alanlar da geri gelmeli.</summary>
    [Fact]
    public void OdemeSatiriGelirTuruVeAciklamayiGosterir()
    {
        var text = Summarize("Payments", PaymentJson);
        Assert.Contains("Gelir Türü: Günlük Yemek", text, StringComparison.Ordinal);
        Assert.Contains("Açıklama: Eylül ayı ödemesi", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// GUID'ler kullaniciya hicbir sey anlatmaz ve satirin tamamini doldurur.
    /// Ekrana hicbiri sizmamali.
    /// </summary>
    [Fact]
    public void OdemeSatiriGuidIcermez()
    {
        var text = Summarize("Payments", PaymentJson);
        Assert.DoesNotContain("d29e1c49", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("7f3b2a10", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1a2b3c4d", text, StringComparison.OrdinalIgnoreCase);
        Assert.False(Regex.IsMatch(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-"),
            $"Cikti GUID benzeri bir dizge iceriyor: {text}");
    }

    /// <summary>Etiketler ham Ingilizce JSON adi degil, Turkce olmali.</summary>
    [Fact]
    public void OdemeEtiketleriTurkce()
    {
        var text = Summarize("Payments", PaymentJson);
        Assert.DoesNotContain("incomeTypeName", text, StringComparison.Ordinal);
        Assert.DoesNotContain("transactionAt", text, StringComparison.Ordinal);
        Assert.DoesNotContain("amount", text, StringComparison.Ordinal);
        Assert.StartsWith("Tarih: 02.09.2026 13:00", text, StringComparison.Ordinal);
    }

    /// <summary>Tarih Turkce bicimde, saniye/milisaniye olmadan.</summary>
    [Fact]
    public void TarihSaniyesizTurkceBicimde()
    {
        var text = Summarize("Payments", PaymentJson);
        Assert.Contains("02.09.2026 13:00", text, StringComparison.Ordinal);
        Assert.DoesNotContain(":00:00", text, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-09-02", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// "İptal edildi: Hayır" gurultudur; olumsuz bayrak ve null alanlar
    /// satirin sonunu "İptal Nedeni: " gibi bos birakmamali.
    /// </summary>
    [Fact]
    public void BosVeOlumsuzAlanlarYazilmaz()
    {
        var text = Summarize("Payments", PaymentJson);
        Assert.DoesNotContain("İptal edildi", text, StringComparison.Ordinal);
        Assert.DoesNotContain("İptal Nedeni", text, StringComparison.Ordinal);
        Assert.False(text.EndsWith(": ", StringComparison.Ordinal),
            $"Satir bos bir etiketle bitiyor: {text}");
    }

    /// <summary>Iptal edilmis odemede bayrak GORUNMELI -- filtre ters kurulmus olmamali.</summary>
    [Fact]
    public void IptalEdilmisOdemeIptalBilgisiniGosterir()
    {
        var text = Summarize("Payments", """
        {"transactionAt":"2026-09-02T13:00:00+03:00","amount":750.00,"incomeTypeName":"Günlük Yemek",
         "isVoided":true,"voidReason":"Yanlış tutar"}
        """);
        Assert.Contains("İptal edildi: Evet", text, StringComparison.Ordinal);
        Assert.Contains("İptal Nedeni: Yanlış tutar", text, StringComparison.Ordinal);
    }

    // DailyTrackingRow: karar 13. alandir, eski bicimlendiricide hep kesilirdi.
    private const string AccessJson = """
    {
      "operationId": "5e6f7a8b-9c0d-4e1f-2a3b-4c5d6e7f8a9b",
      "timestamp": "2026-09-02T12:30:00+03:00",
      "cardNumber": "8350001",
      "studentId": "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
      "studentNo": "5001",
      "studentName": "ELİF ÇETİN",
      "classId": "2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e",
      "className": "6E",
      "mealTypeId": "8a9b0c1d-2e3f-4a5b-6c7d-8e9f0a1b2c3d",
      "mealType": "Öğle Yemeği",
      "deviceId": "4c5d6e7f-8a9b-4c0d-1e2f-3a4b5c6d7e8f",
      "deviceName": "Kantin Turnikesi",
      "decision": "ALLOW",
      "reason": "Hakediş kullanıldı"
    }
    """;

    /// <summary>Gecis gecmisinde karar, cihaz ve ogun bilgisi kaybolmamali.</summary>
    [Fact]
    public void GecisSatiriKararCihazVeOgunuGosterir()
    {
        var text = Summarize("Access History", AccessJson);
        Assert.Contains("Öğün: Öğle Yemeği", text, StringComparison.Ordinal);
        Assert.Contains("Cihaz: Kantin Turnikesi", text, StringComparison.Ordinal);
    }

    /// <summary>ALLOW/DENY kodu ekranda Turkcelesmeli (EnumTextConverter sozlugu).</summary>
    [Theory]
    [InlineData("ALLOW", "Karar: İzin Verildi")]
    [InlineData("DENY", "Karar: Reddedildi")]
    public void KararKoduTurkcelesir(string code, string expected)
    {
        var text = Summarize("Access History", $$"""
        {"timestamp":"2026-09-02T12:30:00+03:00","decision":"{{code}}",
         "deviceName":"Kantin Turnikesi","reason":"Test"}
        """);
        Assert.Contains(expected, text, StringComparison.Ordinal);
        Assert.DoesNotContain(code, text, StringComparison.Ordinal);
    }

    [Fact]
    public void GecisSatiriGuidIcermez()
    {
        var text = Summarize("Access History", AccessJson);
        Assert.False(Regex.IsMatch(text, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-"),
            $"Cikti GUID benzeri bir dizge iceriyor: {text}");
    }

    /// <summary>Kart sekmesi: bool alan duruma uygun Turkce metne cevrilmeli.</summary>
    [Fact]
    public void KartSatiriDurumuAktifPasifOlarakYazar()
    {
        var active = Summarize("Cards", """
        {"id":"d29e1c49-4b1a-4f0e-9c3d-2b7a5e6f1a8c","studentId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
         "studentNo":"5001","studentName":"ELİF ÇETİN","cardNumber":"8350001",
         "validFrom":"2026-09-01T00:00:00+03:00","validTo":null,"replacementReason":null,"isActive":true}
        """);
        Assert.Contains("Kart No: 8350001", active, StringComparison.Ordinal);
        Assert.Contains("Durum: Aktif", active, StringComparison.Ordinal);
        Assert.Contains("Başlangıç: 01.09.2026 00:00", active, StringComparison.Ordinal);
        Assert.DoesNotContain("Bitiş", active, StringComparison.Ordinal);

        var passive = Summarize("Cards", """
        {"cardNumber":"8350000","isActive":false,"replacementReason":"Kayıp/hasarlı kart"}
        """);
        Assert.Contains("Durum: Pasif", passive, StringComparison.Ordinal);
        Assert.Contains("Değiştirme Nedeni: Kayıp/hasarlı kart", passive, StringComparison.Ordinal);
    }

    /// <summary>Veli sekmesi: telefon ve yakinlik gorunmeli, GUID gorunmemeli.</summary>
    [Fact]
    public void VeliSatiriAdTelefonGosterir()
    {
        var text = Summarize("Parents", """
        {"id":"d29e1c49-4b1a-4f0e-9c3d-2b7a5e6f1a8c","studentId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
         "name":"AYŞE ÇETİN","phone":"05321234567","relationship":"Anne","isPrimary":true,"isActive":true}
        """);
        Assert.Equal("Ad Soyad: AYŞE ÇETİN  |  Yakınlık: Anne  |  Telefon: 05321234567  |  Birincil: Evet  |  Durum: Aktif", text);
    }

    /// <summary>Hakedis sekmesi: DateOnly saat icermeden yazilmali, durum Turkcelesmeli.</summary>
    [Fact]
    public void HakedisSatiriTarihVeKalaniGosterir()
    {
        var text = Summarize("Entitlements", """
        {"id":"d29e1c49-4b1a-4f0e-9c3d-2b7a5e6f1a8c","studentId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
         "mealTypeId":"8a9b0c1d-2e3f-4a5b-6c7d-8e9f0a1b2c3d","date":"2026-09-02","quantity":1,
         "consumedQuantity":0,"remainingQuantity":1,"status":"Active","source":"Manual"}
        """);
        Assert.Contains("Tarih: 02.09.2026", text, StringComparison.Ordinal);
        Assert.DoesNotContain("00:00", text, StringComparison.Ordinal);
        Assert.Contains("Kalan: 1", text, StringComparison.Ordinal);
        Assert.Contains("Durum: Aktif", text, StringComparison.Ordinal);
        Assert.Contains("Kaynak: Elle", text, StringComparison.Ordinal);
    }

    /// <summary>Test verisinde bos olan sekmeler icin de alan tanimi calismali.</summary>
    [Fact]
    public void IzinSatiriTurkceEtiketliyazilir()
    {
        var text = Summarize("Leaves", """
        {"id":"d29e1c49-4b1a-4f0e-9c3d-2b7a5e6f1a8c","studentId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
         "startsOn":"2026-09-05","endsOn":"2026-09-07","leaveType":"Mazeret",
         "description":"Sağlık raporu","entitlementBehavior":"Keep"}
        """);
        Assert.Equal("Başlangıç: 05.09.2026  |  Bitiş: 07.09.2026  |  İzin Türü: Mazeret  |  Hakediş: Keep  |  Açıklama: Sağlık raporu", text);
    }

    [Fact]
    public void AktarimSatiriKaynakVeHedefTarihiGosterir()
    {
        var text = Summarize("Holiday/Transfer", """
        {"id":"d29e1c49-4b1a-4f0e-9c3d-2b7a5e6f1a8c","studentId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
         "mealTypeId":"8a9b0c1d-2e3f-4a5b-6c7d-8e9f0a1b2c3d","originalDate":"2026-09-02",
         "targetDate":"2026-09-09","quantity":1,"reason":"Resmi tatil","createdBy":"3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f"}
        """);
        Assert.Equal("Kaynak Tarih: 02.09.2026  |  Hedef Tarih: 09.09.2026  |  Adet: 1  |  Sebep: Resmi tatil", text);
    }

    [Fact]
    public void SmsSatiriDurumuTurkcelestirir()
    {
        var text = Summarize("SMS History", """
        {"id":"d29e1c49-4b1a-4f0e-9c3d-2b7a5e6f1a8c","studentId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d",
         "phone":"05321234567","message":"Yemek hakkı tanımlandı.","status":"Sent",
         "createdAt":"2026-09-02T09:15:00+03:00","error":null}
        """);
        Assert.Contains("Durum: Gönderildi", text, StringComparison.Ordinal);
        Assert.Contains("Tarih: 02.09.2026 09:15", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Hata", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DenetimSatiriIslemVeAciklamayiGosterir()
    {
        var text = Summarize("Audit", """
        {"id":"d29e1c49-4b1a-4f0e-9c3d-2b7a5e6f1a8c","userId":"3c4d5e6f-7a8b-4c9d-0e1f-2a3b4c5d6e7f",
         "timestamp":"2026-09-02T10:00:00+03:00","action":"Update","entityName":"Student",
         "entityId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","description":"Öğrenci güncellendi","affectedRecords":1}
        """);
        Assert.Equal("Zaman: 02.09.2026 10:00  |  İşlem: Update  |  Açıklama: Öğrenci güncellendi  |  Etkilenen Kayıt: 1", text);
    }

    /// <summary>Sekme basliklari ekranda Turkce gorunmeli.</summary>
    [Theory]
    [InlineData("General", "Genel")]
    [InlineData("Cards", "Kartlar")]
    [InlineData("Parents", "Veliler")]
    [InlineData("Entitlements", "Hakedişler")]
    [InlineData("Access History", "Geçiş Geçmişi")]
    [InlineData("Leaves", "İzinler")]
    [InlineData("Holiday/Transfer", "Tatil/Aktarım")]
    [InlineData("Payments", "Ödemeler")]
    [InlineData("SMS History", "SMS Geçmişi")]
    [InlineData("Audit", "Denetim")]
    public void SekmeBaslikTurkce(string key, string expected) =>
        Assert.Equal(expected, StudentTabFormatter.TabTitle(key));

    /// <summary>Taninmayan sekme kimligi kaybolmaz, ham haliyle gorunur.</summary>
    [Fact]
    public void TaninmayanSekmeKimligiAynenDoner() =>
        Assert.Equal("Yeni Sekme", StudentTabFormatter.TabTitle("Yeni Sekme"));

    /// <summary>
    /// Beklenmedik govde icin son care ham dokume duser ama GUID'i yine eler:
    /// veri kaybolmaz, gurultu de ekrana gelmez.
    /// </summary>
    [Fact]
    public void TanimliAlanYoksaHamDokumeDuserAmaGuidElenir()
    {
        var text = Summarize("Payments", """
        {"id":"d29e1c49-4b1a-4f0e-9c3d-2b7a5e6f1a8c","beklenmedikAlan":"deger"}
        """);
        Assert.Contains("beklenmedikAlan: deger", text, StringComparison.Ordinal);
        Assert.DoesNotContain("d29e1c49", text, StringComparison.Ordinal);
    }
}
