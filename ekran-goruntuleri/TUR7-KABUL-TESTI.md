# Tur 7 — Son Kullanıcı Kabul Testi

Soru: *"Tüm sayfaların son kullanıcı testlerini yap, her şey doğru işliyor mu,
her yerden erişiliyor mu?"*

Bu tur iki iş yaptı: eksik olan **kabul testi katmanını** yazdı ve mevcut canlı
testleri gerçekten koşturarak **beş kırık** buldu.

---

## 1. Yeni: `LiveUserAcceptanceTests` (16 test)

Mevcut `LiveSmokeJourney` "ekranlar yükleniyor mu" diye soruyordu. Bu dosya
"**iş gerçekten oluyor mu**" diye sorar.

| Test | Neyi kanıtlar |
|---|---|
| `OgrenciKaydedilirVeSunucudaKalir` | Kayıt formdan sunucuya gidiyor; liste **sunucudan tazelenip** kayıt orada aranıyor |
| `EksikOgrenciFormuAnlasilirHataVerir` | Boş form sessizce kabul edilmiyor; hata sonrası form **açık kalıyor** |
| `TumEkranlaraErisilirVeRotaDegisir` | 13 rotanın hepsine gidiliyor **ve rota gerçekten değişiyor** |
| `AcilistaHicbirEkranHataGostermez` | Açılışta hiçbir ekran hata bayrağıyla gelmiyor |
| `KasaOnaysizTahsilatiKabulEtmez` | Onay kutusu işaretlenmeden Kaydet açılmıyor |
| `TutarTurkceYazimlaDogruOkunur` (4) | `1.250,50` → 1250,50 — nokta ondalık sayılsa **yüz kat** yanlış tutar geçerdi |
| `GecersizTutarReddedilir` (4) | `0`, `-5`, `abc`, boş reddediliyor |
| `YoneticiYazmaDugmeleriniKullanabilir` | İzin varken düğmeler açık — kapalı kalsa okul hiç kayıt giremez |
| `KisayollarGercektenEkranDegistirir` | F2 ve F4 **gerçek pencerede** rotayı değiştiriyor |
| `YenileKisayoluTutarliDavranir` | F5 kullanılabilir diyorsa gerçekten çalışıyor |

**Neden ViewModel'e değil sunucuya bakılıyor:** bu projede daha önce çıkan hata
sınıfı tam olarak "ekran hatasız açılır, düğme çalışır görünür, ama yazma
sunucuya hiç gitmez" idi. ViewModel'in kendi belleğine bakan bir test, yakalamak
istediği hatayı kaçırırdı.

### Testlerin gerçekten koştuğu kanıtlandı

`LiveUiHarness.Run` şöyle başlar: `if (!Enabled) return;` — ortam değişkeni
okunmazsa test **hiçbir şey yapmadan yeşil** olur. Üç mutasyonla bunun olmadığı
kanıtlandı:

| Mutasyon | Sonuç |
|---|---|
| Rota iddiası `"ASLA-OLMAYAN-ROTA"` yapıldı | Kırmızı ✓ |
| `Assert.True(cash.IsAddOpen)` → `Assert.False` | Kırmızı ✓ |
| `Assert.Equal("students", ...)` → `"MUTASYON-ROTA"` | Kırmızı ✓ |

Üçü de geri alındı.

---

## 2. Bulunan kırıklar

### (a) ÜRÜN HATASI — rapor cihaz sütunu metni kesiyordu

`Turnstile` ve `DeniedAccess` raporlarında **CİHAZ** sütunu 125px'e sabitlenmişti.
`Deneme Ethernet Okuyucu` 154px yer istiyor; ad kesiliyordu.

**Etkisi:** Reddedilen geçiş raporunda kullanıcı **hangi cihazın reddettiğini
göremiyordu** — raporun sorduğu asıl sorulardan biri.

Aynı dosyadaki `DailyAccess` zaten `Auto` kullanıyordu; doğru kalıp koddaydı,
iki rapor onu kaçırmıştı.

```
- C("Device", "CİHAZ", "device", 125)
+ C("Device", "CİHAZ", "device", Auto)
```

### (b) TEST HATASI — sabit tarih zaman bombası

`ReportsJourney`: `TodayDate = new(2026, 9, 2)`. Yazıldığı gün yeşil, **ertesi
gün kırmızı**. Ürün doğru çalışıyordu (bugüne sıfırlıyordu), test yanlıştı.
`DateTime.Today` ile ekranın kullandığı kaynağa bağlandı.

### (c) TEST HATASI — tohum verisi bugüne ulaşmıyordu

`LiveSeed`: veri `2026-09-02`'ye kadar üretiliyordu. "Günlük Takip" ve "Pano"
bugünü gösterir; bugün veri olmadığı için `DailyJourney` ve `DashboardJourney`
düşüyordu. Pencere **bugünde bitecek** şekilde kaydırıldı.

`Random(20260902)` tohumu **bilerek sabit bırakıldı** — onu da bugüne bağlamak
öğrenci/sınıf dağılımını her koşuda değiştirir ve sabit değer bekleyen
yolculukları kırardı (dosyadaki mevcut yorumun uyardığı sorun).

### (d) TEST HATASI — tohum saati gelecekte kalıyordu

(c)'yi düzelttikten sonra **iki test birden kırıldı** (`BalanceJourney`,
`SmsAutomationJourney`). Kök neden, tarih kaydırmanın gözden kaçan ikinci
boyutuydu: günü doğru yapmak yetmiyor, **saat** de gerçekçi olmalı.

Tohum çıpası `bugün 08:00` idi. Test gece **02:36**'da koşunca bugünün
188 geçişi **gelecekte** damgalandı. Günlük Takip en yeniden eskiye sıralar
ve sayfa başına 100 satır getirir — ilk sayfa tamamen bu gelecek kayıtlarla
doldu, testin az önce yaptığı gerçek geçiş **görünmez** oldu.

```
- var now = new DateTimeOffset(today.Year, today.Month, today.Day, 8, 0, 0, ...);
+ var now = DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(3)).AddMinutes(-5);
```

Ölçümle doğrulandı: en yeni geçiş `02:31`, şimdi `02:36` — artık geçmişte.

### (e) TEST HATASI — SMS geçmişi sayfa taşması

Tohum artık bugünü kapsadığı için "yarın hakkı bitecek" uyarısı **3 yerine
266 öğrenciye** tetikleniyor. `SmsAutomationJourney` aradığı öğrenciyi
50'lik ilk sayfada bulamıyordu. Test öğrenci filtresini kullanacak şekilde
düzeltildi — 266 satırı gözle tarayan bir kullanıcı da yoktur.

---

## 3. Ürün hatası OLMAYAN bulgular

Bunlar araştırıldı ve **doğru davranış** oldukları kanıtlandı:

| Belirti | Gerçek neden |
|---|---|
| Tam takımda 49 hata | Canlı testler dakikada 20 giriş sınırını aşıyor (`PermitLimit = 20`). Güvenlik özelliği çalışıyor |
| "Kullanıcı adı veya parola geçersiz" | 429'lar başarısız giriş sayılıp hesabı 15 dk kilitlemişti |
| `BalanceJourney` null cihaz | `ShellJourneyDevices` testi tohumlamadan **önce** koşunca `LiveSeed` "cihaz zaten var" deyip kendi cihazlarını eklemiyor — test sırası kırılganlığı |
| `DebounceCancelsStaleResponse` düştü | Kararsız (flaky). Değişikliklerim geri alınıp konulduktan sonra 3 kez arka arkaya geçti |

---

## 4. Sonuç

| Ölçüm | Sonuç |
|---|---|
| Birim testleri | **1956 / 1956 — sıfır hata** (üç ardışık koşu) |
| Yeni kabul testleri | **16 / 16** |
| Canlı yolculuk testleri | **15 grup, tümü yeşil** |
| Gerileme | Yok |

### Önceki teşhisim yanlıştı

Daha önce "5 turnike testi sizin commit edilmemiş çalışmanızdan düşüyor" demiştim.
Doğru değilmiş: gerçek neden **tohum verisinin sabit tarihe bağlı olmasıydı**.
(c) ve (d) düzeltilince o beş test de kendiliğinden geçti ve üç ardışık tam
koşuda sıfır hata alındı. Turnike dosyalarına dokunulmadı.

---

## 5. Kaldırma ve yeniden kurmada veri korunması

Soru: *"Programı kaldırsam veya klasörden silsem bile veri silinmemeli."*

**Cevap: silinmiyor — üç ayrı yolla kanıtlandı.**

**(1) Gerçek MSI'ın içi okundu.** Kaldırmada silinen tek şey:

```
RemoveFile tablosu: RemoveProgramMenuDir | dizin=ProgramMenuDir
```

Yani yalnızca **Başlat menüsü klasörü**. MSI'ın `Directory` tablosunda
`LocalAppDataFolder` ya da `AppDataFolder` **hiç yok** — kurulum veri klasörünü
tanımıyor bile, dolayısıyla silemez.

**(2) İki yol tamamen ayrı:**

| | Yol | Kaldırmada |
|---|---|---|
| Program | `C:\Program Files\YemekhanePro` | Silinir |
| Veri | `%LOCALAPPDATA%\YemekhanePro` | **Dokunulmaz** |

Veri yolu çalışma zamanında `ApplicationDataPath.Resolve()` ile bulunur,
kuruluma gömülü değildir — yeni sürüm de aynı klasörü bulur.

**(3) Üç yeni test kilitledi (mutasyonla kanıtlandı):**

| Test | Mutasyon | Sonuç |
|---|---|---|
| `UninstallingAndReinstallingKeepsTheSchoolsData` | Açılışta veri klasörünü sil | Kırmızı ✓ |
| `UninstallNeverTouchesTheSchoolsDataFolder` | MSI'a veri silen kural ekle | Kırmızı ✓ |
| `DataIsFoundRegardlessOfWhereTheProgramWasInstalled` | — | — |

Artık biri kuruluma veri silen bir kural eklerse test kırılır.

### Güncelleme yolunuz

Kaldır → yeni sürümü kur. Veriniz (`yemekhane.db`, lisans, yedekler, günlükler)
yerinde kalır ve yeni sürüm onu bulur.

**Kaldırmanıza bile gerek yok:** `AllowSameVersionUpgrades="yes"` sayesinde yeni
kurulumu doğrudan üstüne çalıştırabilirsiniz; eski sürüm otomatik kaldırılır.

**Veriyi gerçekten silmek isterseniz** `%LOCALAPPDATA%\YemekhanePro` klasörünü
elle silmeniz gerekir — program bunu asla kendiliğinden yapmaz.
