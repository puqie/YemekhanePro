# Sunucusuz Lisanslama

Aylık sunucu maliyeti olmadan lisans satmak için. Aktivasyon sunucusu **gerekmez**;
anahtar doğrulaması ve makineye bağlama tamamen müşterinin bilgisayarında yapılır.

---

## Nasıl çalışır

Lisans anahtarının **son bloğu imzadır** — imza sırrınızla hesaplanır:

```
YMK-2026-HUG8-CG6C-CZ2C
                  ^^^^ imza
```

Program aynı sırla bu imzayı doğrular. Sırrı bilmeyen geçerli anahtar üretemez,
dolayısıyla "bu anahtarı satıcı verdi mi" sorusu sunucu olmadan yanıtlanır.

Anahtar girildiğinde program lisansı **kendisi üretir** ve o bilgisayarın donanımına
bağlar. Lisans dosyası başka bilgisayara kopyalansa çalışmaz.

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

## 1. İmza sırrınızı üretin (bir kez)

```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
```

Parola yöneticinizde saklayın. **Kaybederseniz sattığınız tüm lisanslar
doğrulanamaz hale gelir.**

## 2. Kurulum dosyasını üretin

Aktivasyon adresini **boş bırakın** — sunucusuz modu bu seçer:

```powershell
$env:YEMEKHANE_LICENSING_SECRET = '<imza sırrı>'
.\scripts\build-installer.ps1 -Version 1.1.0
```

Betik ekrana `Lisans modu: SUNUCUSUZ` yazar. Çıktı:
`artifacts\installer\YemekhanePro-Setup-1.1.0.exe`

## 3. Anahtar üretin ve satın

İki yol var. **Lisans Üretici** penceresi günlük kullanım için daha rahat.

### Yol A — Lisans Üretici penceresi (önerilen)

```powershell
dotnet publish src\Yemekhane.KeyTool -c Release -o C:\LisansUretici
```

`C:\LisansUretici\YemekhaneLisansUretici.exe` dosyasını çift tıklayın.
Masaüstüne kısayol koyabilirsiniz.

İlk açılışta **imza sırrını bir kez** girip Kaydet'e basarsınız; sır bu bilgisayara
Windows'un kendi şifrelemesiyle (DPAPI) kaydedilir, bir daha sormaz. Sonrasında her
satışta yalnızca okul adını yazıp **Anahtar üret** demeniz yeter.

- **Kopyala** düğmesi anahtarı panoya alır — WhatsApp'a yapıştırıp gönderirsiniz
- Alt taraftaki **Satış geçmişi** kime ne sattığınızı tutar
- **Klasörü aç** ile CSV'ye ulaşırsınız (Excel'de açılır)

> Bu programı **müşteriye vermeyin**. İçinde imza sırrı saklanır ve geçerli lisans
> anahtarı üretebilir. Kurulum paketine de dahil edilmez.

### Yol B — Komut satırı

Toplu üretim veya betikten çağırmak için:

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
| Müşteri bilgisayar değiştirdi | Yeni anahtar üretip verin. Eski anahtarı geri alamazsınız (uzaktan iptal yok) |
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
