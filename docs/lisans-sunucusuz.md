# Sunucusuz Lisanslama

Aylık sunucu maliyeti olmadan lisans satmak için. Aktivasyon sunucusu **gerekmez**;
anahtar doğrulaması ve makineye bağlama tamamen müşterinin bilgisayarında yapılır.

---

## İki satış yolu

| | Anahtar | **Lisans dosyası (.lic)** |
|---|---|---|
| Müşteriye giden | Kısa metin | Dosya |
| Ön adım | Yok | Müşteri makine kodunu yollar |
| İkinci bilgisayarda | **Çalışır** ⚠️ | **Çalışmaz** ✅ |
| Ne zaman | Güvendiğiniz müşteri | Kopyalanmasını istemediğinizde |

> **Önemli fark:** Anahtar tekrar kullanılabilir — sunucusuz modda "bu anahtar
> daha önce kullanıldı mı" diye soracak bir merci yoktur. Lisans **dosyası** ise
> üretilirken hedef bilgisayara kilitlenir; kopyalansa bile başka makinede
> çalışmaz. Kopya korumasına önem veriyorsanız dosya yolunu kullanın.

---

## Nasıl çalışır

Lisanslar **özel anahtarınızla imzalanır**, kurulumdaki **açık anahtarla doğrulanır**.
İki anahtar farklıdır: açık anahtar imzayı kontrol eder ama benzerini **üretemez**.

```
  SİZDE            →  imzalar  →   .lic dosyası
  özel anahtar                          ↓
                                   OKULDA
  açık anahtar     →  doğrular  →  (üretemez)
```

Müşteri kurulum klasörünü açıp açık anahtarı okusa bile kendine lisans yazamaz.

> **Neden simetrik (HMAC) değil:** Orada aynı sır hem imzalar hem doğrular,
> dolayısıyla sırrın müşterinin bilgisayarında bulunması zorunluydu. Ölçüldü:
> `appsettings.json` açılıp sır okunabiliyor ve sınırsız geçerli lisans
> üretilebiliyordu. Eski yöntem yalnızca sunucu modu ve daha önce satılmış
> lisanslar için korunuyor.

Lisans ayrıca o bilgisayarın **donanımına bağlanır**; dosya kopyalansa başka
makinede çalışmaz.

## Ne kazanıyorsunuz, ne kaybediyorsunuz

| | Sunucusuz | Sunuculu |
|---|---|---|
| Aylık maliyet | **Yok** | Sunucu kirası |
| İnternet gereksinimi | **Hiç yok** | Aktivasyonda gerekir |
| Anahtar doğrulama | ✅ | ✅ |
| Makineye bağlama | ✅ | ✅ |
| Kurcalama koruması | ✅ | ✅ |
| **Uzaktan iptal** | ❌ | ✅ |
| **Yıllık abonelik** | ❌ | ✅ |
| Sahada kullanım takibi | ❌ | ✅ |

> **Uzaktan iptal yok:** Anahtarı verdikten sonra geri alamazsınız. Ödemeyi
> peşin almanız önerilir.
>
> **Yıllık abonelik yok:** Üretilen lisanslar süresizdir. Abonelik satacaksanız
> sunuculu modu kullanın (`docs/lisans-sunucusu.md`).

---

## 1. Lisans Üretici'yi açın

```powershell
dotnet run --project src\Yemekhane.KeyTool
```

## 2. "Anahtar çifti üret" (bir kez)

Bu, sunucusuz modun kalbi. **Asimetrik** bir çift üretilir:

| Anahtar | Nerede durur | Ne yapar |
|---|---|---|
| **Özel** | Yalnızca bu bilgisayarda, şifreli | Lisans **imzalar** |
| **Açık** | Her kuruluma gömülür | Lisans **doğrular** — üretemez |

Müşteri kurulum klasörünü açıp açık anahtarı okusa bile kendine lisans **yazamaz**.
HMAC (simetrik) yöntemde yazabiliyordu; bu yüzden bırakıldı.

> **Kaybederseniz** sattığınız tüm lisanslar doğrulanamaz hale gelir. Özel anahtar
> yalnızca sizin Windows hesabınızda çözülür.

## 3. "Kurulum exesi üret"

Sürüm kutusu kendiliğinden dolu gelir. Düğmeye basın; açık anahtar otomatik gömülür,
kopyalamanız gerekmez. Birkaç dakika sürer.

Çıktı: `artifacts\installer\YemekhaneProKurulum-<sürüm>.exe` — okula giden **tek dosya**.

## 4. Satış: hangi yol?

| İhtiyaç | Yöntem |
|---|---|
| Belirli bir bilgisayara kilitle (önerilen) | **Lisans dosyası** — aşağıda |
| Serbest anahtar, makine bağı yok | **Anahtar üret** düğmesi |

### Makineye kilitli lisans dosyası

1. Okul, lisans ekranında **"Makine kodunu kopyala"** der ve size yollar
2. Siz: okul adını yazın → **"Panodan al"** → **"Lisans dosyası üret"**
3. Dosya Masaüstü'nüze düşer, okula gönderirsiniz
4. Okul **"Lisans dosyası yükle (.lic)"** ile seçer

Dosya yalnızca o bilgisayarda çalışır; kopyalansa bile başka makinede reddedilir.

### Serbest anahtar

Okul adını yazıp **Anahtar üret** deyin. **Kopyala** ile panoya alıp gönderirsiniz.
Alt taraftaki **Satış geçmişi** kime ne sattığınızı tutar; **Klasörü aç** ile CSV'ye
ulaşırsınız.

> Lisans Üretici'yi **müşteriye vermeyin**. Özel anahtarı saklar ve geçerli lisans
> üretebilir. Kurulum paketine dahil edilmez.

### Komut satırından toplu üretim

Betikten çağırmak için (eski HMAC yöntemi):

```powershell
$env:YEMEKHANE_LICENSING_SECRET = '<AYNI imza sırrı>'
.\scripts\lisans-uret.ps1 -Customer "Atatürk İlkokulu"
```

Çıktı:

```
Anahtar                 Musteri            Tarih
-------                 -------            -----
YMK-2026-HUG8-CG6C-CZ2C Atatürk İlkokulu   2026-09-03
```

Anahtarı müşteriye verin — telefon, WhatsApp, e-posta, fark etmez.

**Toplu üretim ve satış kaydı:**

```powershell
.\scripts\lisans-uret.ps1 -Count 10 -Csv satislar.csv
```

CSV'ye **ekleyerek** yazar; satış kaydınız birikerek gider.

> **Betik çalışmazsa:** Windows betik çalıştırmayı engelliyor olabilir.
> `powershell -ExecutionPolicy Bypass -File .\scripts\lisans-uret.ps1 ...`

## 3b. Makineye kilitli lisans DOSYASI üretmek

Anahtar yerine dosya göndermek isterseniz — dosya başka bilgisayarda çalışmaz:

1. **Müşteri**, programı açar, lisans ekranında **"Makine kodunu kopyala"** der ve
   kodu size yollar (WhatsApp, e-posta).
2. **Siz**, Lisans Üretici'de kodu yapıştırırsınız. Araç kodun hangi bilgisayara ait
   olduğunu gösterir — müşterinin ekranında yazan **Bilgisayar kimliği** ile aynı
   olmalı; farklıysa yanlış kod gelmiş demektir.
3. **Dosya üret** → `Ataturk-Ilkokulu-7266C28AA6B4.lic` kaydedilir.
4. Dosyayı müşteriye yollarsınız; müşteri lisans ekranında
   **"Lisans dosyası yükle (.lic)"** ile seçer.

Dosya adında makine kimliği yazar — bir okula iki bilgisayar satarsanız yanlış
dosyayı göndermezsiniz.

> **Neden "kullanınca kendini silsin" değil:** Müşteri dosyayı açmadan önce
> kopyalarsa (bir dosyayı yedeklemek çok doğaldır) silme hiçbir şey korumaz,
> yalnızca güvenlik yanılsaması yaratır. Makineye kilitlemek kopyalamayı
> **önemsiz** hale getirir: dosya istediği kadar çoğaltılsın, başka bilgisayarda
> matematiksel olarak çalışmaz.

## 4. Müşteri ne yapar

**Yalnızca ilk kurulumda:**

1. Kurulumu çalıştırır
2. Programı açar, lisans ekranında anahtarı girer
3. **Etkinleştir**

İnternet gerekmez. Program lisansı üretip o bilgisayara bağlar ve diske kaydeder.

**Sonraki her açılışta:** lisans ekranı **gelmez**. Kullanıcı doğrudan
**kullanıcı adı ve şifresini** girip programa girer — tıpkı her gün olduğu gibi.

> Lisans anahtarı **bir kez** girilir, sonra unutulur. Her açılışta sorulan şey
> kullanıcı adı/şifredir. (`AnahtarYalnizcaBirKezGirilir` testiyle kilitli.)

---

## Sık karşılaşılan durumlar

| Durum | Ne yapmalı |
|---|---|
| "Lisans anahtarı geçersiz" | Anahtar yanlış yazılmış ya da **başka bir sırla** üretilmiş. Kurulumu ürettiğiniz sır ile anahtarı ürettiğiniz sır aynı olmalı |
| Müşteri bilgisayar değiştirdi | Yeni makine kodunu isteyip yeni dosya üretin. Eskisi yeni bilgisayarda zaten çalışmaz |
| "Bu lisans başka bir bilgisayara ait" (dosya) | Dosya başka makine için üretilmiş. Müşteriden makine kodunu yeniden isteyin |
| "Kod okunamadı" | Makine kodu eksik kopyalanmış. Müşteriden kodun **tamamını** yeniden göndermesini isteyin |
| "Bu lisans başka bir bilgisayara ait" | Lisans dosyası kopyalanmış. Donanım bağı çalışıyor demektir |
| Müşteri format attı | Aynı anahtar çalışır — donanım aynı kaldığı sürece |
| Anakart değişti | Donanım imzası bozulur; yeni anahtar gerekir |

---

## Sonradan sunucuya geçmek

Karar kalıcı değil. Sunucu koymaya karar verirseniz:

1. Sunucuyu kurun (`docs/lisans-sunucusu.md`)
2. Kurulumu adresi vererek yeniden üretin:
   ```powershell
   .\scripts\build-installer.ps1 -Version 1.2.0 -ActivationUri "https://lisans.siteniz.com/"
   ```

**Aynı imza sırrını kullanın** — o zaman sattığınız eski anahtarlar geçerliliğini korur.

---

## Güvenlik notu

Sunucusuz olmak korumasız olmak değildir. Şunlar aynen çalışır:

- **İmza doğrulaması** — lisans dosyası elle düzenlenirse yakalanır
- **Donanım bağı** — başka bilgisayara kopyalanamaz
- **Saat kontrolü** — bilgisayarın saati geri alınırsa yakalanır

Bunların hepsi testle kilitli (`OfflineActivationTests`). Anahtar imzasının
satış betiği ile ürün arasında ayrışmadığı da ayrıca test ediliyor —
ayrışırsa sattığınız anahtarlar sahada reddedilirdi.
