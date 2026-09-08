# YemekhanePro — Kurulum Adımları

İki taraf var: **siz (satıcı)** ve **okul**. Sizin tarafınız bir kere yapılır.

---

## BÖLÜM A — Sizde (tek sefer)

### A1. Lisans Üretici'yi açın

```powershell
dotnet run --project src\Yemekhane.KeyTool
```

### A2. "Anahtar çifti üret" düğmesine basın

Bir kez basılır, ömür boyu aynı çift kullanılır.

- **Özel anahtar** bu bilgisayarda şifreli saklanır, ekranda hiç görünmez
- **Açık anahtar** kurulumlara otomatik gömülür — kopyalamanız gerekmez

> **Dikkat:** Sonradan "Yeni çift üret" derseniz daha önce sattığınız **tüm lisanslar geçersiz olur**. Program bunu sorar.
>
> Özel anahtar yalnızca **sizin Windows hesabınızda** çözülür. Anahtar çifti ayrıca depoda
> `secrets/lisans-anahtar-cifti.key` dosyasında tutulur (sahibin kararı). Bilgisayar değiştirince
> yeni çift üretmeyin: yeni bilgisayarda `scripts\lisans-anahtar-geri-yukle.ps1` çalıştırın, aynı
> çiftle devam edin; dağıtılmış lisanslar geçerli kalır. **Bu dosyaya erişen herkes lisans
> üretebilir; depoyu özel tutun.**

### A3. "Kurulum exesi üret"

Sürüm kutusu kendiliğinden dolu gelir (bir önceki sürümden bir sonrası). Düğmeye basın, birkaç dakika bekleyin.

Bittiğinde Gezgin açılır ve dosya seçili gelir:

```
artifacts\installer\YemekhaneProKurulum-1.0.0.exe
```

**Okula göndereceğiniz tek dosya budur.** İçinde .NET, API, masaüstü, veritabanı — hepsi var.

---

## BÖLÜM B — Okulun bilgisayarında

### B1. Kurulumu çalıştırın

Exe'ye çift tıklayın → İleri → Kur. Yönetici izni ister.

| Ne | Nerede |
|---|---|
| Program | `C:\Program Files\YemekhanePro\` |
| Veriler | `%LOCALAPPDATA%\YemekhanePro\` |

Veriler **programı kaldırsanız da silinmez** — güncelleme yapabilirsiniz.

### B2. Makine kodunu alın

İlk açılışta lisans ekranı gelir. **"Makine kodunu kopyala"** düğmesine basıp size gönderirler (WhatsApp, e-posta — fark etmez).

Kod tek satırdır, şuna benzer:

```
YMK1.AalcQOjEaaHJcNGY3F8yjqqvVDTnzTddN5_ubuw...==.74B1B0
```

---

### B3. Siz: lisans dosyasını üretin

Lisans Üretici'de:

1. **Okul adını** yazın
2. **"Panodan al"** — kodu kopyaladıysanız kutuya kendisi yapıştırır
3. **"Lisans dosyası üret"**

Dosya **Masaüstü'nüze** kaydedilir ve Gezgin'de seçili açılır. Okula gönderin.

> Bu dosya **yalnızca o bilgisayarda** çalışır. Başka makineye kopyalasalar reddedilir.

### B4. Okul: dosyayı yükler

Aynı ekranda **"Lisans dosyası yükle (.lic)"** → dosyayı seçer → program açılır.

> Etkinleştirme kutusuna bir şey **yazılmaz**. O kutu anahtar içindir; dosya ayrı düğmeyle yüklenir.

### B5. İlk yönetici girişi

Program ilk açılışta bir yönetici parolası **üretip ekranda gösterir**. **Not alın — bir daha gösterilmez.**

Dolu bir veritabanında bu adım atlanır; ekran bunu söyler.

---

## BÖLÜM C — Turnike ve kart okuyucu (SC403)

### C1. Öğün saatlerini girin
Tanımlar → Öğünler. **Tek öğün** varsa saat sorulmaz; her okutma o öğünü düşer. Birden çok
öğün varsa her birine **başlangıç ve bitiş saati** yazın (örnek: Öğle 11:30 – 13:30); program
hangi öğünün düşüleceğini bu saatten anlar. Hiçbir öğün o saati kapsamıyorsa okutma Günlük
Takip'te "Şu saatte tanımlı öğün yok" reddi olarak görünür, hak düşmez, turnike açılmaz.

### C2. Cihazı ekleyin
Cihazlar → Yeni cihaz:

| Alan | Değer |
|---|---|
| Tür | SC403 |
| Bağlantı | Ethernet |
| IP | Cihaz ekranındaki adres (örnek `169.254.198.201`) |
| Port | `4370` |
| Turnike bağlı | **İşaretli** (işaretli değilse turnike hiç sürülmez) |
| Yön | Giriş |
| Otomatik bağlan | İşaretli |

Kaydedince durum "Bağlı" olmalı. Bilgisayarın Ethernet kablosu doğrudan cihaza takılıysa
`169.254.x.x` adresi kendiliğinden gelir; modem gerekmez.

### C3. Eski programı kapatın ve cihaz belleğini temizleyin
Aynı cihazı iki program birden dinlememeli. Eski program cihaza kartları yüklemişti; cihaz o
kartları kendi belleğinden tanıyıp **kendi başına "Teşekkürler" der ve geçiş verir**, bu programın
kararını beklemez. Cihazlar / Turnikeler → cihaz kartında **Belleği Temizle → Temizlemeyi Onayla**.
Bundan sonra kararı yalnızca bu program verir; cihaz kart tutmaz.

### C3a. Kart numarası hangisi?
Kartın **arkasındaki** uzun sayı (örnek `0008247129 125,55129`): virgülden önceki sayı, baştaki
sıfırlar olsa da olmasa da olur (`8247129` ya da `0008247129`). Öndeki kısa sayı (örnek 6296)
okulun bastığı sıra numarasıdır, çipin numarası değildir. Öğrenci kartındaki **Kart No** alanına
çip numarasını, **Baskı No** alanına öndeki kısa numarayı yazın; masa tipi okuyucu gerekmez.
Kayıp bir kart bulununca arama kutusuna öndeki numarayı (6296) yazın; sahibi çıkar.

**Kart Yükleme Durumu sayfası** SC403 için "Kart yüklenmez; geçiş kararı programda" der. Bu
normaldir: SC403'e kart yüklenmez, okutma programa gelir ve karar orada verilir.

### C4. Deneyin
Kayıtlı bir kart okutun: Günlük Takip ekranında geçiş görünür, turnike döner. Kayıtsız kart
"Kart tanımsız" ile reddedilir. Cihazın saatini de ayarlayın; program kendi saatini kullanır
ama cihazın kendi kayıtları yanlış saatle kalır.

## BÖLÜM D — Yıl sonu ve temizlik

### D1. Yıl sonu sıfırlama
Ayarlar → **Yıl Sonu** sekmesi. "Etkilenecekleri Göster" sayımı verir; onay kutusuna `SIFIRLA`
(Türkçe karakter olmadan, büyük harf) yazınca "Yılı Sıfırla" açılır. Silinen: kartlar, hakedişler,
yemek kullanımları, geçiş kayıtları, hak devirleri, izinler, grup üyelikleri, SMS ve toplu işlem
geçmişi. **Silinmeyen:** öğrenci sicilleri (pasife alınır), veliler, tahsilat ve bakiye hareketleri;
veli 1-2 yıl önceki ödemesini sorarsa Raporlar → **Gelir** (tarih aralığı) ya da öğrenciyi
"Pasif" filtresiyle bulup **Ödemeler** sekmesi. Kalan: öğünler, sınıflar, cihazlar, kullanıcılar,
tatil takvimi, şablonlar, ayarlar. Sıfırlamadan önce otomatik güvenlik yedeği alınır; alınamazsa
hiçbir şey değişmez. Yeni yılın öğrenci listesi **Sicil Aktar** ile yüklenir; listedeki numaralar
aynı sicili yeniden aktif eder, kartlar dosyadaki kart sütunundan yeniden yazılır.
Not: Okul öğrenci numarasını yeni bir öğrenciye yeniden verirse eski sicil o numarayla
güncellenir; numaralar öğrenciye özel kalmalıdır.

### D2. Cihaz silme
Cihazlar / Turnikeler → kartın üzerinde **Sil** → **Silmeyi Onayla**. Yanlış eklenen cihaz kalıcı
silinir. Geçiş kaydı olan cihaz silinmez (kayıtlar korunur); onu **Pasifleştir**.

### D3. Öğrenci silme
Öğrenciler → öğrenciyi açın → sicil kartında **Sil** → **Silmeyi Onayla**. Kayıt listelerden
kaybolur; Sicil Aktar ile aynı numara yeniden yüklenirse geri gelir.

### D4. Tatil ekleme ve silme
Takvim → günü seçin → **Tatil oluştur**. Formda **Başlangıç** ve **Bitiş (dahil)** vardır; bayram
veya yarıyıl için aralığı seçin, her gün ayrı kayıt olur ve "Tatiller" bloğundan **Bu günü sil**
ya da **Tüm aralığı sil** ile kaldırılır (iki adımlı onay). Aynı gün ve kapsamda ikinci tatil
kabul edilmez. Tatil kaydı hakları kendisi değiştirmez; **Hakediş etkilerini toplu uygula**
sihirbazı aralığın tamamıyla açılır.

## BÖLÜM E — SMS (Mutlucell)

### E1. Sağlayıcıyı tanımlama
Ayarlar → **SMS** → Sağlayıcı: **Mutlucell (XML SMS ağ geçidi)**. Kullanıcı adı (ka), API şifresi
(pwd) ve Mutlucell'de onaylı başlık (org) girilir; adres boş bırakılır (smsgw.mutlucell.com
kullanılır). **Kaydet**. Kuyruk gönderimi yeni ayarı uygulama yeniden başlatılınca kullanır.

### E2. Test SMS ve kontör
Aynı sekmede **SMS sınama** kartı: alıcı GSM yazıp **Test SMS Gönder**. Sonuç kutusunda
sağlayıcının ham yanıtı görünür: `$88512` gibi bir değer paket numarasıdır (başarılı); `23`
kullanıcı adı/şifre hatası, `21` başlık hesaba ait değil, `22` kontör yetersiz, `34` API kapalı.
**Kontör Sorgula** kalan kontörü gösterir. Test kaydedilmiş ayarlarla gider; form kirliyse
düğmeler kapalıdır.

### E3. Şablonlar ve otomatik SMS
SMS Merkezi → Şablonlar ilk açılışta 6 hazır şablonla gelir (Yemek Ücreti Hatırlatma, Yemek
Hakkı Bitiyor, Ödeme Alındı, Yemekhane Girişi, Kart Yenilendi, Genel Bilgilendirme); silinen
şablon geri gelmez. Veliye günlük hak uyarısının **gönderim saati** Ayarlar → SMS → Otomatik SMS
→ "Gönderim saati (SS:dd)" alanındadır; program o saatte açık değilse ilk açılışta o günün
uyarısı gider, ertesi güne sarkmaz.

## Sonraki makineler

B1–B4 her bilgisayar için tekrarlanır. Makine kodu her makinede farklıdır, dolayısıyla her biri kendi `.lic` dosyasını ister.

## Güncelleme

A3'ü yeni sürüm numarasıyla tekrarlayın, okulda üstüne kurun. **Veri de lisans da yerinde kalır.**

Açık anahtar aynı kaldığı için eski lisanslar çalışmaya devam eder.

---

## Sorun giderme

| Belirti | Sebep | Çözüm |
|---|---|---|
| "Kurulum üretimi için aracı proje klasöründen çalıştırın" | Araç yayınlanmış klasörden açılmış | `dotnet run --project src\Yemekhane.KeyTool` |
| "Kod okunamadı" | Kodun bir kısmı kopyalanmış | Okuldan **tamamını** yeniden istersiniz |
| "Bu lisans başka bir bilgisayara ait" | Dosya yanlış makineye üretilmiş | Doğru makine koduyla yeniden üretin |
| Kurulum üretimi başarısız | Ayrıntı açılan pencerede | Günlüğü okuyun; genelde açık dosya kilidi |
