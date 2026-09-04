# Donanım Entegrasyonu — ZKTeco SC403 + ÖZAK 720 E

Bu belge sahadaki fiziksel ekipmanın yazılım tarafındaki karşılıklarını ve kurulumda
**cihaz başında doğrulanması gereken** noktaları tanımlar.

Kaynak önceliği (donanım dokümantasyonu §07): cihaz etiketi → üretici ürün sayfası →
üretici datasheet → üretici kılavuz → üretici SDK → SDK manual → üçüncü taraf.

---

## 1. Ekipman

| Cihaz | Model | Rol |
|---|---|---|
| Geçiş kontrol terminali | ZKTeco SC403 | Kartı okur, kapı rölesini sürer |
| Turnike | ÖZAK 720 E | Bel tipi tripod; röle ile açılır |
| Kart | 125 kHz RFID proximity | SC403 dahili okuyucusu |

### SC403 — üretici tarafından belgelenen değerler (§01.2)

- ID kart kapasitesi: **30.000** → `Sc403Adapter.MaxCardCapacity`
- İşlem kayıt kapasitesi: **50.000** → `Sc403Adapter.MaxTransactionCapacity`
- Haberleşme: RS485, TCP/IP, USB-Host
- Dahili okuyucu: 125 kHz RFID proximity
- Güç: DC 12 V / 3 A · Okuma mesafesi: 5–10 cm

> Bu uygulama **TCP/IP** kullanır. RS485 ve USB-Host, cihaz başında doğrulanmadan
> desteklendiği iddia edilemez (§08).

### ÖZAK 720 E — üretici tarafından belgelenen değerler (§04.3)

- Kontrol: **kuru kontak veya TTL/CMOS**
- Kontrol gerilimi: **5–48 V**
- Gövde: 304 paslanmaz · Ölçüler: 1060 × 955 × 300 mm + kol
- Çalışma sıcaklığı: −17 °C / +68 °C

---

## 2. Mimari karar: turnike neden bir cihaz nesnesi değil?

ÖZAK 720 E'nin **IP adresi, seri portu veya komut kümesi yoktur.** Üretici dokümanına göre
kontrol girişi kuru kontaktır: bir röle kontağı kapanınca turnike açılır.

Bu yüzden turnike `IDevice` olarak modellenmez. Yazılımdaki karşılığı
`OzakTurnstileProfile` adlı bir **fiziksel profildir** ve turnikeyi süren cihaza
(SC403) aittir:

```
Kart okutulur
   → SC403 kartı okur          (IZkTecoSdk.ReadRealTimeCardsAsync)
   → Yazılım geçiş kararını verir (AccessDecisionService)
   → Karar olumluysa SC403 kapı rölesi kapanır
   → ÖZAK 720 E açılır
```

Turnikeye ayrı bir ağ protokolü uydurmak, donanım dokümantasyonu §08 ile doğrudan
çelişirdi. Kod tarafında bu karar bir testle korunur:
`HardwareIntegrationTests.OzakProfileIsNotModelledAsNetworkDevice`.

### Cihaz türü seçimi

| Kurulum | `DeviceType` | `HasTurnstile` | Üretilen sınıf |
|---|---|---|---|
| Yalnızca kart okuma | `SC403` | `false` | `Sc403Adapter` |
| Turnike süren terminal | `SC403` | `true` | `Sc403AccessController` |

---

## 3. Kart numarası

Kart üzerine **basılı numara ile cihazın okuduğu RFID değeri aynı olmak zorunda değildir**
(§10). Yazılım basılı numaradan RFID değeri türetmez.

`ZkTecoCardNumber` yalnızca karşılaştırmayı güvenli hale getirir: aynı fiziksel kart,
firmware sürümüne göre `0008573921` veya `8573921` olarak gelebilir; eşitlik normalize
edilmiş değer üzerinden kurulur.

**Kurulumda doğrulanmalı:** kart cihaza okutulup SC403'ün döndürdüğü değer, sisteme
kaydedilecek numara olarak alınmalıdır.

---

## 4. AÇIK NOKTA — kapı rölesini süren SDK çağrısı

> **DEVICE VALIDATION REQUIRED**

ZKTeco Standalone SDK Development Manual'de (v2.1 / v2.2) adı geçen fonksiyonlar
arasında **kapı rölesini süren bir çağrı bulunmamaktadır.** Dokümanda karşılığı olmayan
bir fonksiyon adı uydurmak §08 ile yasaktır.

Bu nedenle `Sc403AccessController.GrantAccessAsync`, sessizce başarılı sayılmak yerine
`ZK_DEVICE_VALIDATION_REQUIRED` hatası döndürür. `TurnstileService` bu başarısızlığı
**REVIEW_REQUIRED** yoluna sokar ve tüketilen yemek hakkını iade eder — yani doğrulama
tamamlanmadan hiçbir öğrenci hak kaybetmez.

**Kapatmak için gereken:** cihaz başında, üreticinin SDK'sı (`zkemkeeper.dll`, 32-bit COM;
`regsvr32` ile kaydedilmeli) üzerinden kapı rölesini süren çağrı doğrulanmalı ve
`IZkTecoSdk` uygulamasına eklenmelidir. `IZkTecoSdk`'nin diğer tüm üyeleri dokümanda adı
geçen fonksiyonlarla birebir eşleşir.

### Ayrıca sahada doğrulanacaklar

| Konu | Durum | Nerede ayarlanır |
|---|---|---|
| Kapı rölesi SDK çağrısı | **UNKNOWN** | Yukarıdaki madde — kod değişikliği gerekir |
| Röle darbe süresi | **UNKNOWN** | Cihaz ekranı → "Turnike sürüş ayarları" |
| Turnike yön kilidi | Kuruluma göre | Cihaz ekranı → "Turnike çift yönlü" |
| Kart elektronik varyantı (ID / MiFare) | **UNKNOWN** | Kart okutularak belirlenmeli |
| TCP portu | Varsayılan 4370 | Cihaz ekranı → "Port" |

---

## 6. Şube kurulum ekranı

Kurulumu yapan şube, **Cihazlar** ekranından aşağıdakileri girip sonradan
düzenleyebilir — kod değişikliği gerekmez:

| Alan | Geçerlilik |
|---|---|
| Ad | Zorunlu, en fazla 100 karakter, benzersiz |
| Tür | SF300 / SC403 / ComReader / EthernetReader |
| IP adresi | Geçerli IP; port ile birlikte benzersiz |
| Port | 1–65535 (SC403 varsayılanı 4370) |
| COM portu / Baud | COM1–COM256 · 300–4.000.000 (yalnız ComReader) |
| Konum | En fazla 150 karakter |
| Yön | Entry / Exit / Bidirectional |
| Aktif · Otomatik bağlan | Evet/Hayır |
| **Turnike bağlı** | Röleye turnike bağlıysa işaretlenir |
| **Röle darbe süresi** | 50–5000 ms (yalnız turnike bağlıyken) |
| **Turnike çift yönlü** | Mekanik yönlendirme iki yöne de izin veriyorsa |

> **Önemli:** "Turnike bağlı" işaretlenmezse cihaz yalnızca kart okuyucu olarak kurulur
> (`Sc403Adapter`) ve turnikeyi **açamaz**. Röleye turnike bağlıysa bu kutu işaretlenmelidir.

Turnike ayarları yalnızca "Turnike bağlı" işaretliyken görünür ve kaydedilir; kutu
kaldırılırsa değerler temizlenir, böylece cihaz sonradan turnikeye bağlandığında eski bir
değer sessizce geri gelmez.

Girilen değerler doğrudan donanım adaptörüne ulaşır
(`TurnstileDriveSettingsTests.EnteredSettingsReachTheHardwareAdapter` bunu doğrular).

> Röle darbe süresi 50–5000 ms ile sınırlandırılmıştır. Uzun darbe, kontağın turnike bir
> sonraki geçişe hazır olduktan sonra da kapalı kalmasına — yani tek okutmayla birden
> fazla kişinin geçmesine — yol açar.

---

## 5. Hata kodları

Sınıflandırma **satıcıya göre değil, sebebe göre** yapılır (`DeviceErrorCodes`).
Kodlar `SATICI_SEBEP` biçimindedir ve son eke göre eşleşir; böylece yeni bir cihaz
ailesi eklendiğinde sessizce sınıflandırma dışında kalmaz.

- **Kalıcı** (yeniden denenmez): `INVALID_CARD`, `MEMORY_FULL`, `UNSUPPORTED`,
  `CAPABILITY`, `DEVICE_VALIDATION_REQUIRED`
- **Kopma** (turdaki kalan kartlar denenmez): `DISCONNECTED`, `CONNECT_FAILED`,
  `CONNECT_TIMEOUT`, `WRITE_FAILED`, `NOT_CONFIGURED`

SDK bağlaması yapılandırılmamışsa her komut `ZK_SDK_NOT_CONFIGURED` ile reddedilir;
kart hiçbir zaman "yüklendi" olarak işaretlenmez.
