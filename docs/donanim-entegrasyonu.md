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

> **UYGULANDI (2026-09-07): ham ZK protokolü, UDP 4370 — `ZkProtocolSdk`**

Uzun süre `IZkTecoSdk`'nin **hiç uygulaması yoktu**: üretimde fabrika `null` veriyordu,
adaptör ağa tek bayt göndermeden her komutu `ZK_SDK_NOT_CONFIGURED` ile reddediyordu ve
`GrantAccessAsync` sabit olarak başarısız dönüyordu. Sahada "cihaz bağlanamıyor" bunun
sonucuydu; cihaz sağlamdı.

Üretici SDK'sı (`zkemkeeper.dll`) 32-bit COM olduğu için 64-bit API'ye bağlanamaz. Bunun
yerine cihazın **tel protokolü** doğrudan uygulandı (`src/Yemekhane.Devices/ZkTeco/Protocol/`).
§08'in "uydurma" yasağı korunur: hiçbir paket biçimi tahmin edilmedi; iki bağımsız,
yaygın kullanılan açık kaynak uygulama (**pyzk**, **node-zklib**) referans alındı ve
checksum/comm key vektörleri Python ile bayt bayt doğrulandı. Referansların ortak tuhaflığı
(checksum eski `reply_id` ile hesaplanır, pakete `reply_id+1` yazılır) birebir kopyalanır.

| İşlev | Protokol komutu | zkemkeeper karşılığı |
|---|---|---|
| Bağlan / şifre / ayrıl | CONNECT 1000, AUTH 1102, EXIT 1001 | Connect_Net, Disconnect |
| Kimlik | GET_VERSION 1100, OPTIONS_RRQ 11 | GetFirmwareVersion, GetSerialNumber |
| Durum | GET_FREE_SIZES 50 | GetDeviceStatus |
| Kart olayı | REG_EVENT 500 (0x0400 kart numarası + EF_ATTLOG) | RegEvent + OnHIDNum / OnAttTransactionEx |
| Kart yazma/silme | USER_WRQ 8, DELETE_USER 18 | SetUserInfo, DeleteUserInfoEx |
| Kullanıcı tablosu | DATA_WRRQ 1503 + READ_BUFFER 1504 | GetAllUserInfo |
| **Kapı rölesi** | **UNLOCK 31** (süre × 100 ms) | **ACUnlock** |

**Neden UDP:** ZEM500 tabanlı eski firmware (Linux 2.4.20) 4370'i yalnızca UDP dinler;
TCP sondası "kapalı" döner ve cihaz bozuk sanılır — sahada tam olarak bu oldu.

**Kullanıcı eşlemesi:** cihaz kullanıcıyı 16-bit `uid` + PIN ile tutar; bizim
`externalUserId` bir GUID'dir ve sığmaz. Cihaza `PIN = uid`, ad = kart numarası yazılır;
kart↔uid eşlemesi cihazın kendi tablosundan okunur. Sistem kartın kime ait olduğunu
zaten veritabanından bilir; cihazdan yalnızca **kart numarası** gelmesi yeter.

**Cihaz reddi istisnadır, başarısız sonuç değil:** `DeviceCardPushWorker` sonucun
`Succeeded` alanına bakmaz; başarısız sonuç dönülseydi reddedilen kart "yüklendi"
işaretlenirdi.

Röle sürülemezse `GrantAccessAsync` yine **başarısız SONUÇ** döner (istisna değil):
`TurnstileService` yalnızca bu yolda tüketilen yemek hakkını iade eder.

### Ayrıca sahada doğrulanacaklar

| Konu | Durum | Nerede ayarlanır |
|---|---|---|
| Kapı rölesi çağrısı | Uygulandı (UNLOCK 31); **darbenin turnikeyi döndürdüğü sahada görülmeli** | Cihaz ekranı → "Turnike sürüş ayarları" |
| Röle darbe süresi | **UNKNOWN** — 500 ms ile başlayın, dönmezse artırın | Cihaz ekranı → "Turnike sürüş ayarları" |
| Turnike yön kilidi | Kuruluma göre | Cihaz ekranı → "Turnike çift yönlü" |
| Gerçek zamanlı olay bayt düzeni | **Ölçüldü (2026-09-08):** 0x0400 kart (5 bayt) + 0x0001 geçiş kaydı (10 bayt); aşağıya bakın | Çözülemeyen olay tanı günlüğüne düşer |
| Kullanıcı kaydı boyutu (28/72) | Cihazdan ölçülür; `Devices:ZkUserRecordSize` ile zorlanabilir | `appsettings.json` |
| İletişim şifresi (comm key) | Varsayılan 0; cihaz ACK_UNAUTH dönerse gerekir | `Devices:ZkCommKey` |
| Yanıt zaman aşımı | 3 sn; `OperationTimeoutSeconds`'tan kısa olmalı | `Devices:ZkReplyTimeoutSeconds` |
| UDP portu | Varsayılan 4370 (TCP değil) | Cihaz ekranı → "Port" |

### Sahada ölçülen (2026-09-08, `zk-dinle.ps1`)

Cihaz: **SC403/M**, platform ZEM500, firmware `Ver 6.60 Aug 17 2015`, seri `6079154700038`,
yalnız kart okuyucu (`~IsOnlyRFMachine=1`), 444 kullanıcı, 29.378 geçiş kaydı. UDP 4370'te
**şifresiz** ACK_OK döndü (`Devices:ZkCommKey` gerekmedi).

- **Her okutmada iki olay gelir.** Önce `0x0400` (kart numarası; 5 bayt = uint32 LE + dolgu,
  örnek `59 D7 7D 00 00` = 8247129), ~1,3 sn sonra `0x0001` (geçiş kaydı; 10 bayt `<HBB6s>`,
  örnek PIN 3030 + cihaz saati). SDK `0x0400`'ü doğrudan kart olarak yükseltir (tabloya bakmaz,
  kayıtsız kartı da taşır) ve 3 sn penceresi içindeki geçiş kaydını **yankı** sayıp atlar;
  aksi halde bir okutma iki geçiş sayılır, öğün hakkı iki kez düşerdi. Pencere dışında kalan
  geçiş kaydı (kart olayı UDP'de kaybolmuşsa) yine yükselir.
- **Cihaz saati** bilgisayardan 83 dakika gerideydi. Olaylar **bilgisayar saatiyle**
  damgalanır; sapma tanı günlüğüne bir kez düşer. Cihazın saatini ayarlamak yine de önerilir.
- Alarm olayı `0x0200` (kod 55) görüldü; kart değildir, atlanır.
- **Boru hattı kopuktu:** `TurnstileService.ProcessCardReadAsync`'in üretimde hiçbir çağıranı
  yoktu; kart okunsa bile olay kimseye ulaşmıyor, turnike açılmıyordu. `TurnstileCardReadWorker`
  turnikeli her okuyucuyu dinler; `TurnstileCardHandler` öğünü sunucu saatine ve öğün
  tanımındaki saat penceresine göre seçer (`MealWindowResolver`) ve boru hattına verir.
  Ayar: `Devices:TurnstileReader` (`SupervisionIntervalSeconds`, `RestartDelaySeconds`).
  **Öğün tanımlarına saat aralığı girilmelidir**; girilmezse tek öğün her saat geçerli sayılır,
  birden çok öğün varsa okutma "tanımlı öğün yok" uyarısıyla düşer.
- Tanı betiğindeki PowerShell tuzağı (`[byte] -shl 8` bayt içinde taşar) ACK_OK'u (0x07D0)
  `cmd=208` gösteriyordu: cihaz konuşuyordu, betik okuyamıyordu.

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
