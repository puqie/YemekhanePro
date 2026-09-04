# Tur 2 — Uçtan uca canlı denetim (ekle / düzenle / sil / iptal / filtre / dışa aktar)

**Yöntem:** Altı paralel ajan, her biri kendi worktree'si + kendi API portu + kendi tohumlu veritabanıyla, kendi ekran grubunda gerçek iş akışlarını ViewModel üzerinden gerçek API'ye karşı sürdü; bulduğu hatayı kök nedenden düzeltti, regresyon testi yazdı ve düzeltmeyi geri alarak testin gerçekten kırıldığını kanıtladı. Sonra altı dal ana dalda birleştirildi ve **26 canlı yolculuk** temiz bir veritabanına karşı ana dalda tekrar koşuldu (hepsi yeşil). Birim testi: **1640/1640**.

Ekran görüntüleri bu klasörde: `students-*`, `cash-*`, `ent-*`, `bulk-*`, `cal-*`, `sms-*`, `settings-*`, `import-*`, `reports-*`, `daily-*`, `dashboard-*`, `devicecards-*`, `shell-*`, `giris-*`, `cihaz-*`.

## Veri hataları (kullanıcıya yanlış sonuç üretiyordu)

| Ekran | Hata | Kök neden |
|---|---|---|
| Kasa | `1250.50` girilince **125.050 TL** kaydediliyordu (100 kat) | tr-TR ayrıştırması noktayı binlik sayıyor; yeni ayrıştırıcı: tek nokta yalnızca 3'lü gruplar ayırıyorsa binlik |
| Kasa | Günlük Kasa'da "Göster" BUGÜN kartını eziyordu | iki görünüm aynı özelliği paylaşıyordu |
| Öğrenciler | **Düzenle → Kaydet sınıf/şubeyi siliyordu** | PUT yalnızca no/ad/soyad gönderiyor, sunucu kalan alanları null yazıyordu |
| Öğrenciler | Türkçe arama çalışmıyordu (`ali` → 0, `öz` → 0) | ham ad üzerinde SQLite `LIKE` (yalnızca ASCII duyarsız); artık normalleştirilmiş `SearchName` |
| Öğrenciler | "Pasifleştir" aslında **siliyordu** (geri alınamaz) | DELETE + global filtre; artık IsActive, silme ayrı ve iki adımlı onaylı |
| Hakedişler / Toplu işlem | Toplu işlem geçmişi **her zaman 500**; sihirbaz uygulanan işlemi "uygulanamadı" diye bildiriyordu | SQLite `DateTimeOffset` ORDER BY desteklemez; `JulianDay` ile sıralama |
| Hakedişler | İptal edilmiş satır "KALAN 1", özet kalanı iptalleri sayıyordu | durum filtresi eksikti |
| Takvim | Ay/gün özeti iptal edilmiş hakları sayıyordu | aynı |
| Raporlar | Gelir raporu = Günlük Kasa raporu (aynı sorgu) | Günlük Kasa artık gün × tür kırılımı |
| Raporlar | Kart Hareketleri tarih filtresini yok sayıyordu | istemci `start/end`, sunucu `startDate` bekliyordu; bilinmeyen parametre artık 400 |
| Raporlar | Dışa aktarmada ham kodlar (ALLOW, VOIDED, "OPEN / ") | CSV/PDF/Excel çeviriden geçmiyordu |
| SMS | Küçük harfle arama boş dönüyordu; birincil işaretlenmemiş veli "telefon yok" sayılıyordu | harf duyarlı `instr`; yalnızca `IsPrimary` |
| Sicil Aktar | TELEFON sütunu doğrulanıp atılıyordu (veli açılmıyor → SMS gitmiyor) | uygulama adımı veliyi yazmıyordu |
| Ayarlar | Sayısal alanlarda harf/negatif sessizce yutuluyor, `25:99` saati 00:00 olarak kaydediliyordu | binding sessiz reddediyor; `TimeOnly.MinValue` varsayılanı |
| Kabuk | **Enter kısayolu hiç çalışmıyordu** (palette sonucu açılamıyordu) | `Key.Enter.ToString()` "Return" döner, tablo "Enter" bekliyordu |
| Kabuk | Bildirim zamanı UTC gösteriliyordu; cihaz bildirimi Dashboard'a düşüyordu | ofset çevrilmiyordu; `devices/{id}` rotası eşleşmiyordu |
| Kabuk | Canlı rozet API döndükten sonra bir daha bağlanmıyordu | SignalR 3 denemeden sonra pes ediyor, kimse tekrar denemiyordu |
| Kabuk | Token dolunca tek çıkış uygulamayı kapatmaktı (açık form kaybı) | yeniden giriş katmanı yoktu; eklendi, form korunur |

## İş akışı hataları (sessiz başarısızlık / yanlış davranış)

- Sunucu doğrulama mesajları (`Gelir türü adı zaten kayıtlı.`, `Günlük öğün hakkı 1-10…`, `SMS şablon adı zaten kayıtlı.`) kullanıcıya ulaşmıyor, ekran "Çevrimdışı"ya düşüyordu — Kasa, Hakedişler, Takvim, SMS, Ayarlar, Sicil Aktar istemcilerinde `ApiRequestException` ile mesaj taşınıyor.
- Manuel hedef GUID istiyordu (Hızlı Hakediş, Toplu işlem) — artık öğrenci numarasıyla.
- Öğrenci kayıt/pasif/kart sonrası seçim kayboluyor, form boşalıyordu; iptal düğmesi yoktu; kartsız öğrenciye ilk kart verilemiyordu.
- Kasa'da iki çekmece aynı anda açılabiliyor, iptal sonrası eski seçim kalıyor (404), "Seçileni Düzenle" demeden Kaydet mevcut türü sessizce yeniden adlandırıyordu.
- SMS'te manuel seçim aramalar arası kayboluyor, pasif şablon gönderim listesine düşüyordu (404).
- Sihirbaz uygulayınca arkadaki liste/takvim eski kalıyordu.
- Günlük Takip açılır kutuları her yüklemede seçimi sıfırlıyordu (filtre uygulanır uygulanmaz uçuyordu).
- "Şimdi yükle" cihaz çevrimdışıyken hiçbir şey söylemiyordu.

## Ayırt edicilik (aynı adlı öğrenciler)

Global arama, Kasa doğrulama/iptal paneli, Hakediş iptal onayı, sihirbaz önizlemesi, bekleyen kart listesi ve SMS alıcı listesi artık **no + sınıf/şube + kart** gösteriyor.

## Tasarım

Öğrenciler sağ paneli yeniden kuruldu (form + 6 eylem düğmesi + sabit sıralı 2 satır sekme, hepsi ekranda); Ayarlar iki sütunlu kart yerleşimi (dikey kaydırma kalktı); rapor tablosu 1440px'e sığar, ad/no/kart sütunları içeriğe göre ölçülür; tarih kutularında takvim ikonu kesmesi; boş açılır kutularda "Tümü"; kalan ham İngilizce değerler (Manual, None, Daily, Sunday, Provider, Trip, Keep, Delete, Success/Error, Person…) Türkçeleşti; giriş penceresinde düğme pencere dışına taşmıyor.

## Sizin dosyalarınızda bulunan, DOKUNULMAYAN bulgular (Cihazlar)

1. `DeviceAdministrationService.cs:155` — `GET api/devices/{id}/logs` **500**: `OrderByDescending(x => x.Timestamp)` (`DateTimeOffset`) SQLite'ta çevrilemiyor. Aynı hata toplu işlem geçmişinde vardı; çözüm `JulianDay` sıralaması (bkz. `EfBulkOperationRepository.cs`). "Loglar" düğmesi bu yüzden hiç çekmece açmıyor.
2. `DeviceApiClient.cs:60` + `DevicesViewModel.cs:176` — doğrulama hataları ham ProblemDetails JSON'u olarak görünüyor; `title` ayrıştırılmalı (diğer istemcilerdeki `ApiErrors.ReadAsync` deseni hazır).
3. `DevicesViewModel.cs:103-104`, `DevicesView.xaml:51,54` — Tür (`EthernetReader/ComReader/SF300/Simulator`) ve Yön (`Entry/Exit/Bidirectional`) ham İngilizce; `EnumTextConverter`'a sözlük + `ItemTemplate` yeter.
4. `DevicesView.xaml:52-53` — Port/Baud `int`'e bağlı; harf yazılınca sessizce reddedilir, eski değer kaydedilir.
5. `DevicesViewModel.cs:179-180` — `catch {}` tüm istisnaları yutuyor (401 bile "pasifleştirilemedi" görünür).

## Kapsam dışı bırakılanlar

- "Kolonlar" menüsünden gizlenen sütun dışa aktarmaya yansımıyor (sunucu sabit sütun seti; API sözleşmesi değişikliği).
- `ApiExceptionHandler.cs:10` 500'leri loglamıyor (teşhis için geçici log eklenip geri alındı).
- Lisans mesajları (`Yemekhane.Licensing`) Türkçe karaktersiz ("Lutfen…").
- `users-roles` rotası: API'de RBAC var, masaüstü ekranı yok; kasıtlı gizli, sabit açıklamalı.
