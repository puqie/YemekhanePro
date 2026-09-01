# Lisanslama ve Kopya Koruma — Tasarım

Tarih: 2026-09-01
Durum: Onaylandı (uygulama planı bekliyor)

## Amaç

YemekhanePro'nun lisanssız kopyalanmasını zorlaştırmak; lisansı satıcının
(bizim) kontrolümüzde tutmak. Mevcut kullanıcı adı/şifre sistemi korunur;
lisans onun **üzerine** bir katman olarak eklenir.

## Kapsam kararları

Bu kararlar kullanıcı tarafından onaylandı:

| Karar | Seçim |
|---|---|
| Doğrulama modeli | Online aktivasyon + çevrimdışı tolerans |
| Çevrimdışı tolerans | 30 gün (son 7 günde uyarı) |
| İhlal davranışı | Tam kilit; yalnızca aktivasyon ekranı, veri silinmez |
| Mevcut giriş sistemi | Değişmez; lisans ekranı önüne eklenir |
| Aktivasyon sunucusu | Şimdilik yalnızca istemci; sunucu arayüz arkasında |
| Koruma | Sunucu doğrulaması + IL obfüskasyonu (VMProtect kullanılmayacak) |

## Mimari

### Yeni proje: `Yemekhane.Licensing`

**Proje referansı YOKTUR.** Yalnızca .NET taban kütüphanelerini kullanır.

Gerekçe: lisans, kalıcılıktan daha alt seviye bir konudur.
`Yemekhane.Infrastructure`'a bağlanmak (a) bağımlılık yönünü ters çevirir,
(b) EF Core'u lisans projesine sürükler, (c) Infrastructure derlemesini
değiştirerek lisans kontrolünü atlamayı kolaylaştırır.

Bu yüzden `WindowsDpapiSecretProtector` yeniden KULLANILMAZ; lisans projesi
kendi DPAPI sarmalayıcısını taşır (~15 satır) ve **kendi entropisini**
kullanır (`YemekhanePro.License.v1`). Mevcut ayar entropisi
(`OkulYemek.SystemSettings.v1`) DEĞİŞTİRİLMEZ — değişirse sahadaki
şifreli ayarlar okunamaz hâle gelir.

### Açılış sırası

Mevcut sıraya ekleme yapılır, sıra değiştirilmez:

```
Tek örnek kilidi            (mevcut)
   ↓
Lisans kontrolü             (YENİ)
   ├─ Geçerli   → devam
   └─ Geçersiz  → Aktivasyon penceresi
        ├─ Aktive edildi → devam
        └─ Vazgeçildi    → uygulama kapanır
   ↓
Yerel API başlat            (mevcut)
   ↓
Giriş ekranı                (mevcut, dokunulmaz)
   ↓
Ana pencere                 (mevcut)
```

Lisans kontrolü **yerel API başlamadan önce** yapılır: lisanssız kurulumda
veritabanı, turnike ve zamanlayıcı servisleri hiç ayağa kalkmaz.

## Bileşenler

### 1. Donanım parmak izi — `IHardwareFingerprint`

Üç bileşen toplanır:

| Bileşen | Kaynak |
|---|---|
| Anakart seri no | `Win32_BaseBoard.SerialNumber` |
| Sistem diski seri no | `Win32_DiskDrive.SerialNumber` |
| Makine GUID | `HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid` |

**2/3 eşleşme kuralı.** Üç bileşenden ikisi tutuyorsa aynı makine sayılır.

Gerekçe: katı eşleşme müşteriyi disk/anakart değişiminde mağdur eder;
tek bileşen ise sanal makineye kopyalanır. 2/3 dengeyi kurar.

Bileşenler ham hâlde diske YAZILMAZ; her biri ayrı ayrı SHA-256 ile
hash'lenip saklanır. Böylece lisans dosyası çalınsa bile donanım kimliği
sızmaz.

Okunamayan bileşen (WMI erişimi yok, sanal disk) boş sayılır ve eşleşme
sayılmaz. Üç bileşenin de okunamadığı durumda parmak izi üretilemez ve
aktivasyon reddedilir — sessizce "her makine geçerli" duruma DÜŞÜLMEZ.

### 2. Yerel lisans deposu — `ILicenseStore`

Konum: `ApplicationDataPath.Resolve()` altında `license.dat`.
Koruma: DPAPI, `DataProtectionScope.LocalMachine`.

Saklanan alanlar:

| Alan | Amaç |
|---|---|
| `LicenseKey` | Lisans anahtarı |
| `CustomerName`, `Edition` | Görüntüleme |
| `FingerprintHashes` | Makineye bağlama (2/3 için üç hash) |
| `IssuedAt`, `ExpiresAt` | Abonelik penceresi |
| `LastValidatedAt` | Çevrimdışı sayacın başlangıcı |
| `Signature` | Sunucu imzası (kurcalama tespiti) |

### 3. Saat manipülasyonuna karşı koruma

Kullanıcı sistem saatini geri alırsa 30 günlük sayaç sıfırlanmamalıdır.

Kural: **`LastValidatedAt` asla geriye gitmez.** Kaydedilenden daha erken
bir sistem saati görülürse bu kurcalama sayılır ve lisans geçersizleşir.

Bu ayrıntı olmadan 30 günlük tolerans pratikte sonsuza döner; tasarımın
zorunlu parçasıdır.

### 4. Aktivasyon istemcisi — `ILicenseActivationClient`

```
Task<ActivationResult> ActivateAsync(string licenseKey, HardwareFingerprint fingerprint, CancellationToken ct)
Task<ValidationResult> ValidateAsync(StoredLicense license, CancellationToken ct)
```

Uygulamalar:
- `HttpLicenseActivationClient` — gerçek; uç nokta yapılandırılabilir
- `FakeLicenseActivationClient` — testler ve sunucu hazır olmadan geliştirme

Sunucu hazır olduğunda yalnızca bu sınıf değişir.

**Sunucu sözleşmesi** (sunucu yazılırken uyulacak):

```
POST /activate   { licenseKey, fingerprints[], productVersion }
  200 → { customerName, edition, issuedAt, expiresAt, signature }
  409 → başka makinede aktif
  404 → anahtar yok
  410 → iptal edilmiş

POST /validate   { licenseKey, fingerprints[], signature }
  200 → { expiresAt, signature }     (LastValidatedAt yenilenir)
  410 → iptal edilmiş                 (lisans hemen geçersizleşir)
```

Ağ hatası ile "iptal edildi" AYRILIR: ağ hatasında çevrimdışı toleransa
düşülür, iptalde lisans anında geçersizleşir. Bu ayrım kritiktir —
karıştırılırsa ya iptal işe yaramaz ya internet kesintisi müşteriyi kilitler.

### 5. Lisans servisi — `LicenseService`

Karar tablosu:

| Durum | Sonuç |
|---|---|
| Lisans dosyası yok | `NotActivated` |
| İmza tutmuyor | `Tampered` |
| Parmak izi 2/3 tutmuyor | `WrongMachine` |
| `ExpiresAt` geçmiş | `Expired` |
| Sunucu "iptal" dedi | `Revoked` |
| Saat geri alınmış | `Tampered` |
| Çevrimdışı > 30 gün | `OfflineGracePeriodExceeded` |
| Çevrimdışı > 23 gün | `Valid` + uyarı |
| Diğer | `Valid` |

`Valid` dışındaki her sonuç aktivasyon ekranını açar.

### 6. Aktivasyon penceresi — `ActivationWindow`

Mevcut `LoginWindow` desenini izler (marka kimliği, Türkçe, ShutdownMode
tuzağına dikkat — bkz. hafıza: WPF sessiz kapanma).

İçerik:
- Durum açıklaması (neden kilitli olduğu **anlaşılır** dille)
- Lisans anahtarı girişi
- "Aktive Et" düğmesi
- Makine kimliği (destek için kopyalanabilir)
- Hata durumunda somut mesaj ("Bu lisans başka bir bilgisayarda kullanımda"),
  "Bir hata oluştu" DEĞİL

## Koruma katmanları

VMProtect **kullanılmayacak**. Gerekçe: VMProtect'in sanallaştırma motoru
native x86/x64 kod içindir; .NET IL'ini sanallaştıramaz. .NET tarafında
yapabildiği yalnızca paketlemedir ve IL çalışma anında bellekte açık
bulunur. Ödenen bedele karşılık kazanılan koruma, yarattığı yanlış güven
duygusunu haklı çıkarmıyor; ayrıca paketleme antivirüs yanlış pozitifleri
ve hata ayıklama zorluğu getirir.

Korumanın gerçek kaynağı sunucudur:

1. **Sunucu doğrulaması** — asıl dayanıklılık burada. İstemci ikili dosyası
   yamalansa bile sunucu imzası özel anahtar olmadan üretilemez. Lisans
   iptali anında etkili olur.
2. **İmzalı lisans dosyası** — yerel dosya kurcalanırsa imza tutmaz.
3. **IL obfüskasyonu** (ConfuserEx, ücretsiz) — isim karıştırma ve kontrol
   akışı gizleme. Sıradan bir kullanıcının dnSpy ile açıp lisans kontrolünü
   bulmasını zorlaştırır.
4. **Tek nokta olmaması** — lisans durumu tek bir `if` ile değil, birkaç
   yerden sağlanır.

Beklenti: "kolay kırılmaz". "Kırılamaz" değil — .NET'te kırılamaz yoktur ve
bunu iddia eden her çözüm yanlış güven verir.

Obfüskasyon **en son adımdır**: build sürecini erken bozmamak için lisans
altyapısı ve testleri bittikten sonra uygulanır.

## Test kapsamı

Projenin kuralı gereği her madde gerçekten test edilir:

- Parmak izi 2/3: bir bileşen değişince geçer, iki değişince geçmez
- Üç bileşen de okunamıyorsa aktivasyon reddedilir (sessiz geçiş yok)
- Saat geri alma → `Tampered`
- 30 gün dolunca kilit; 23. günde uyarı, hâlâ `Valid`
- Ağ hatası → çevrimdışı tolerans; sunucu "iptal" → anında geçersiz
- İmza kurcalanmış dosya → `Tampered`
- Geçersiz anahtar → anlaşılır Türkçe hata
- **Lisanssız açılışta yerel API'nin hiç başlamadığı**

## Kapsam dışı

- Aktivasyon sunucusunun kendisi (ayrı iş)
- Lisans satış/faturalama akışı
- Ağ üzerinden çoklu istemci lisansı (şu an makine başına)
