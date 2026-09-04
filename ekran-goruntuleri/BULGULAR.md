# Arayüz Denetim Raporu — Gerçek Veriyle

**Tarih:** 2026-09-02
**Yöntem:** Gerçek API (127.0.0.1:5255) + gerçek veri (420 öğrenci, 373 kart, 280 veli, 5.530 hakediş, 260 gelir işlemi, 1.440 erişim logu) ile 12 ekran + 26 etkileşim durumu yakalandı ve incelendi.
**Veri özelliği:** Aynı ad-soyadlı öğrenciler kasıtlı olarak üretildi (ADA KATIRCI / ADA HAŞLAMACI / ADA SÖYLEMEZ, dört farklı ALİ) — ayırt edicilik testi için.

---

## A. VERİ HATALARI (kullanıcıya yanlış bilgi gösteriyor)

### A1. Raporlarda TUTAR sütunu her zaman ₺0,00 — KÖK NEDEN BULUNDU
**Kanıt:** API `{"amount": 500}` döndürüyor, ekran ₺0,00 gösteriyor. Özet satırı ₺9.900,00 diyor.
**Kök neden:** `src/Yemekhane.Application/Reports/ReportContracts.cs:62-64`
```csharp
[System.Text.Json.Serialization.JsonIgnore]
public long AmountCents { get; init; }
public decimal Amount => AmountCents / 100m;   // hesaplanmış — init edilemez
```
`AmountCents` `[JsonIgnore]` olduğu için deserialize edilmiyor; `Amount` hesaplanmış özellik olduğu için set edilemiyor. Sonuç: masaüstünde her zaman 0.
**Etki:** Günlük Kasa ve Gelir raporlarında hiçbir işlemin tutarı görünmüyor. Muhasebe raporu kullanılamaz.
**Not:** Kasa ekranı ayrı bir sözleşme (`CashContracts.cs:15 decimal Amount`) kullandığı için etkilenmiyor — orada tutarlar doğru.

### A2. "Gelir" raporu ile "Günlük Kasa" raporu birebir aynı
**Kanıt:** `GET /api/reports/DailyCash` ve `GET /api/reports/Income` aynı tarih için aynı 100 kaydı, aynı sırayla döndürüyor.
**Etki:** Kullanıcı menüde iki ayrı rapor görüyor, ikisi de aynı şeyi gösteriyor.

### A3. Kart Hareketleri raporu tarih filtresini yok sayıyor
**Kanıt:** `startDate=2026-09-02&endDate=2026-09-02` isteğine `2026-08-03` ve `2026-08-02` tarihli kayıtlar dönüyor.
**Etki:** Ekran "Toplam 0 / kayıt bulunamadı" gösteriyor ama API veri döndürüyor — tutarsızlık.

---

## B. AYIRT EDİCİLİK (aynı adlı öğrenci sorunu — açıkça talep edilen gereksinim)

### B1. Öğrenciler tablosunda ayırt edici sütunlar kesik
**Kanıt:** `v-students.png` — 12 sütun 745px'e sığmıyor:
| Sütun | Görünen | Olması gereken |
|---|---|---|
| NO | `500'` | `5001` |
| KART | `835(` | `8350001` |
| VELİ TEL | `053:` | `05339132562` |
| DURUM | `Ak` | `Aktif` |
| BUGÜNKÜ HAK | `BUGÜ` | — |
| SON GİRİŞ | `02.0` | `02.09.2026` |

Ekranda üst üste **ALİ ÖZTÜRK 7-E** ve **ALİ ÖZTÜRK 7-B** var; ayırt edici olan kart numarası okunamıyor.

### B2. SMS alıcı listesinde sadece ad-soyad var
**Kanıt:** `v-sms.png` — dört satır üst üste "ADA AKGÜN" (no 5356, 5016, 5375, 5252). Sınıf/şube/kart gösterilmiyor.
**Etki:** Yanlış veliye SMS gitme riski.

### B3. Öğrenci seçilince sağdaki form BOŞ kalıyor
**Kanıt:** `x-student-selected.png` — ELİF ÇETİN seçili, ancak "Öğrenci NO / Ad / Soyad" kutuları boş.
**Kök neden:** `StudentsViewModel.cs:206` — `Form*` alanları yalnızca `OpenEdit()` içinde, yani "Düzenle"ye basılınca doluyor.
**Etki:** Kullanıcı hangi öğrencinin seçili olduğunu formdan göremiyor.

---

## C. TÜRKÇE OLMAYAN METİNLER (kullanıcıya görünen)

| Nerede | Görünen | Kaynak |
|---|---|---|
| Öğrenci detay sekmeleri (9 adet) | `Cards`, `Parents`, `Entitlements`, `Access History`, `Leaves`, `Holiday/Transfer`, `Payments`, `SMS History`, `Audit` | `StudentsViewModel.cs:197` |
| Günlük Takip → Karar filtresi ve rozetler | `ALLOW`, `DENY` | `DailyTrackingViewModel.cs:64` |
| Hakedişler → DURUM / KAYNAK | `Active`, `Manual` | API değeri, çevrilmiyor |
| Raporlar → KARAR / DURUM | `ALLOW`, `Active`, `ACTIVE`, `VOIDED` | API değeri, çevrilmiyor |
| Ayarlar → sekme | `Sync` | `SettingsView.xaml:76` |
| Ayarlar → SMS/Sync alan etiketi | `Endpoint` | `SettingsView.xaml` |
| Ayarlar → Yedekleme değerleri | `Daily`, `Sunday` | ComboBox kaynağı |
| Ayarlar → Sync durumu | `Disabled` | API değeri |
| Ayarlar → Loglar | `Information`, `Error`, `Faulted`, `Reconnecting` | API değeri |
| Ayarlar → Bağlantılar cihaz durumu | `Error`, `Reconnecting`, `Offline` | Cihazlar ekranında Türkçe — **tutarsız** |
| SMS → alıcı kapsamı | `Manual` | ComboBox kaynağı |
| SMS → şablon değişkenleri | `{{StudentName}}`, `{{ParentName}}`, `{{ExpiryDate}}`, `{{EntryTime}}`, `{{Amount}}` | Şablon tanımı |
| Cihaz editörü | `EthernetReader`, `Entry`, `Development simulator` | ComboBox kaynağı |

---

## D. KESİLEN METİN

| Ekran | Sütun/Alan | Görünen |
|---|---|---|
| Öğrenciler | 6 sütun (bkz. B1) | — |
| Hakedişler | TARİH | `09.09.202` |
| Hakedişler | ÖĞÜN | `Öğle Yem` |
| Raporlar (0,4,5,8) | TARİH | `02.09.2026 11:58:00.(` |
| Raporlar (4,5) | AÇIKLAMA | `...Eylül ayı öder` |
| Ayarlar → Loglar | Seviye | `Informatic` |
| Ayarlar → Loglar | Kaynak | `Device/Yemekhane Gir` |
| Ayarlar → Loglar | Özellikler | JSON ortadan kopuyor, yatay kaydırma da yok |
| SMS → Geçmiş | Sütun başlığı | `Denem` |

**Ortak kök neden:** Sütun genişlikleri sabit piksel (`Width="145"` gibi), içeriğe göre değil. Aynı anda SINIF gibi tek karakterlik sütunlar ~90px israf ediyor.

---

## E. DÜZEN VE HİZALAMA

### E1. Öğrenci detay sekmeleri 3 satıra sarıyor ve sıra karışıyor
`x-student-selected.png` — 420px panelde 10 sekme sığmıyor. `General` sekmesi üçüncü satırda görünüyor, oysa ilk sekme.

### E2. Öğrenci form paneli "Sınıf" etiketinden sonra kesiliyor
Etiket var, altındaki alan yok — sekme bloğu yerini almış. Şube/Kart No/Veli Tel/Durum/Not alanları hiç görünmüyor. Eylem düğmeleri (Pasifleştir, İzin Ver, SMS Gönder, Hakediş Ver, Okuyucudan Al, Kart Değiştir) da görünmüyor.

### E3. Takvim ay gezinme düğmeleri ekranın iki ucunda
`v-calendar.png` — `‹` solda, `›` en sağda, arada tüm başlık ve kapsam seçici var.

### E4. Günlük Takip'te saat milisaniyeli
`11:58:00.000` — gereksiz hassasiyet, yer kaplıyor.

### E5. Raporlarda yatay kaydırma çubuğu son satırı örtüyor
Veri dolu 5 rapordan 4'ünde; kaydırma çubuğu tablonun alt satırının üzerine biniyor.

### E6. Hakedişler filtre alanlarının etiketi yok
6 boş kutu, sadece ToolTip var — fareyle üzerine gelmeden ne olduğu anlaşılmıyor.

### E7. SMS Gönder sekmesinde 6 etiketsiz girdi alanı
İki boş açılır liste + üç boş metin kutusu + arama kutusu.

### E8. SMS Geçmiş sekmesinde 4 etiketsiz filtre alanı

### E9. Ayarlar sekmelerinde içerik 760px'e sıkışmış
Ekranın sağ ~%35'i boş; Yedekleme sekmesinde buna rağmen dikey kaydırma çıkıyor.

---

## F. RENK TUTARSIZLIĞI

Marka rengi turuncu (`AccentBrush`). `Primary` stili mevcut ve 5 yerde doğru kullanılıyor, ancak 5 yerde elle boyanmış:

| Dosya:Satır | Düğme | Sorun |
|---|---|---|
| `StudentImportView.xaml:106` | İçe Aktar | **MAVİ** (`InfoBrush`) — marka dışı |
| `SettingsView.xaml:29` | Kaydet | Elle `AccentBrush` — hover/disabled durumları kayıp |
| `ReportsView.xaml:58` | Uygula | Elle `AccentBrush` |
| `SmsView.xaml:43` | SMS'leri kuyruğa al | Elle `AccentBrush` |
| `DevicesView.xaml:63` | Kaydet | Elle `AccentBrush` (kullanıcının değiştirdiği dosya — dokunulmadı) |

Sonuç: "Kaydet" düğmesi üç ekranda üç farklı görünümde (turuncu / soluk turuncu / beyaz).

---

## G. YAZIM

- Ayarlar → Yedekleme: onay metni **"GERİ YUKLE"** (Ü eksik). Kullanıcı bunu birebir yazacaksa hangi yazımın kabul edileceği belirsiz.

---

## H. ÖLÜ KOD / ERİŞİLEMEYEN ÖZELLİK

- `ShellRoutes.UsersRoles` (`users-roles`) tanımlı ve Ayarlar'da "Kullanıcılar / Roller" düğmesi ona gitmeye çalışıyor, ancak bu rota `App.xaml.cs:146-156`'da **hiçbir zaman kayıtlı rotalara eklenmiyor** ve karşılık gelen bir View yok. Düğme `CanNavigateUsers` ile gizlendiği için çökme olmuyor, ama özellik erişilemez durumda.

---

## Doğrulanmayan / araç kaynaklı olduğu tespit edilen iddialar

Bunlar ilk taramada hata gibi görünüp incelemede **hata olmadığı** anlaşıldı:

- "Ayarlar ekranı tamamen boş" → ekran görüntüsü aracının `PageShell` stilini yükleyememesinden. Gerçekte 6 sekme düzgün çalışıyor.
- "Sicil Aktar'da örtüşen metin ve boş kırmızı kutu" → aynı araç sınırı.
- "Öğrenci Kullanımı / Sınıf Yemek / Turnike raporları boş" → benim test verimde `meal_usage` ve `turnstile_events` tabloları boş. Uygulama hatası değil.
- "Menü hiç görünmüyor" → ilk ekran görüntüleri sayfaları pencere olmadan çekmişti. Menü doğru çalışıyor.

---

## I. ÖĞRENCİ DETAY SEKMELERİ (10 sekme) — DERİN İNCELEME

### I1. Sekme içerikleri ham veritabanı dökümü, asıl bilgi DÜŞÜYOR — KÖK NEDEN BULUNDU
**Kök neden:** `src/Yemekhane.Desktop/Services/StudentServices.cs:139-141`
```csharp
private static string Summarize(JsonElement value) => string.Join("  |  ", value.EnumerateObject()
    .Where(x => x.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Object and not JsonValueKind.Array)
    .Take(6).Select(x => $"{x.Name}: {x.Value.ToString()}"));
```
Genel amaçlı bir JSON dökümcüsü: alan adlarını **ham İngilizce** basıyor ve **yalnızca ilk 6 alanı** alıyor.

**Ödemeler sekmesinde ne olduğu (ölçüldü):**

| Sıra | Alan | Durum |
|---|---|---|
| 1 | `id` (GUID) | görünür |
| 2 | `operationId` (GUID) | görünür |
| 3 | `studentId` (GUID) | görünür |
| 4 | `studentName` | görünür |
| 5 | `cardNumber` | görünür |
| 6 | `transactionAt` | görünür |
| 7 | `incomeTypeId` | **düşer** |
| 8 | `incomeTypeName` = "Günlük Yemek" | **düşer** |
| 9 | **`amount` = 750,00 TL** | **düşer** |
| 10 | `description` = "Eylül ayı ödemesi" | **düşer** |

İlk 6 alanın 3'ü kullanıcıya hiçbir şey ifade etmeyen GUID; **tutar, gelir türü ve açıklama görünmüyor.**
Aynı sorun Erişim Geçmişi sekmesinde: karar (ALLOW/DENY), cihaz ve öğün bilgisi ilk 6'ya girmediği için düşüyor.

### I2. Detay sekmelerinde hiçbir eylem düğmesi yok
Kart Değiştir, Okuyucudan Al, Hakediş Ver, İzin Ver, SMS Gönder, Pasifleştir — hiçbiri sekmelerde yok. Sekmeler salt okunur döküm; öğrenci bazlı hiçbir iş yapılamıyor.

### I3. Boş sekmelerde "kayıt yok" mesajı yok
İzinler, Tatil/Aktarım, SMS Geçmişi, Denetim sekmeleri bomboş beyaz alan. Kullanıcı yüklenmedi mi, kayıt mı yok anlayamıyor.

### I4. Sekme şeridi her tıklamada yeniden diziliyor
420px panelde 10 sekme 3 satıra sarıyor ve **seçilen sekmenin satırı değiştiği için diğer sekmelerin yeri zıplıyor**. Kullanıcı komşu sekmeyi aynı yerde bulamıyor.

### I5. "Düzenle" düğmesi öğrenci seçiliyken bile gri
Seçim formu beslemediği için (bkz. B3) düzenleme açılamıyor.

---

## TEST VERİSİ KAYNAKLI — UYGULAMA HATASI DEĞİL

Bunlar incelemede hata gibi göründü, ancak benim ürettiğim test verisinin özelliği:

- **Veli telefonu 12 hane** (`053022563341`) — seed aracım hatalı üretmiş; uygulama doğrulaması değil.
- **Öğrenci Kullanımı / Sınıf Yemek / Turnike raporlarının boş olması** — `meal_usage` ve `turnstile_events` tablolarını doldurmadım.
- **Denetim (Audit) sekmesinin boş olması** — kayıtları API üzerinden değil doğrudan veritabanına yazdığım için denetim kaydı oluşmadı.
