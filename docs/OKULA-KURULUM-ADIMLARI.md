# Okula Kurulum — Adım Adım

Bir okula sıfırdan kurulum yaparken izleyeceğiniz sıra.

---

## ÖNCE: Kurulum dosyasını üretin (bir kez)

> Bunu her okul için tekrar yapmanıza gerek yok. Bir kez üretip aynı `.exe`
> dosyasını bütün okullara kurarsınız.

### 1. İmza sırrınızı üretin ve saklayın

```powershell
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))
```

Çıkan metni **parola yöneticinize kaydedin**.

> **Kaybederseniz sattığınız tüm lisanslar doğrulanamaz hale gelir.** Yeniden
> üretemezsiniz; her müşteriye yeni kurulum + yeni lisans göndermeniz gerekir.

### 2. Kurulum dosyasını üretin

```powershell
$env:YEMEKHANE_LICENSING_SECRET = '<1. adımdaki sır>'
.\scripts\build-installer.ps1 -Version 1.2.0
```

Ekranda `Lisans modu: SUNUCUSUZ (aktivasyon adresi boş)` yazmalı.

Çıktı: `artifacts\installer\YemekhanePro-Setup-1.2.0.exe` (~101 MB)

### 3. Lisans Üretici'yi kurun (kendi bilgisayarınıza)

```powershell
dotnet publish src\Yemekhane.KeyTool -c Release -o C:\LisansUretici
```

`C:\LisansUretici\YemekhaneLisansUretici.exe` → masaüstüne kısayol yapın.

İlk açılışta imza sırrını **bir kez** girip Kaydet deyin.

> Bu programı **müşteriye vermeyin.** İçinde imza sırrı var.

---

## OKULA KURULUM

### 4. Programı kurun

`YemekhanePro-Setup-1.2.0.exe` → çift tıkla → **Evet** (yönetici) → İleri → Kur → Son

> `.msi` dosyasını **çift tıklamayın**; yönetici yükseltmesi isteyemediği için
> sessizce iptal olur. Kuracağınız dosya `.exe` olandır.

Masaüstünde ve Başlat menüsünde simge oluşur.

### 5. Lisansı verin

Programı ilk açtığınızda **lisans ekranı** gelir. İki yolunuz var:

#### Yol A — Anahtar (hızlı, güvendiğiniz okul)

1. Lisans Üretici'yi açın
2. Okul adını yazın → **Anahtar üret**
3. **Kopyala** → okula gönderin
4. Okul anahtarı girer → **Etkinleştir**

> Anahtar **tekrar kullanılabilir**: aynı anahtarla ikinci bir bilgisayar da
> lisanslanabilir. Kopyalanmasını istemiyorsanız Yol B'yi kullanın.

#### Yol B — Lisans dosyası (kopyalanamaz)

1. **Okul**, lisans ekranında **"Makine kodunu kopyala"** der, kodu size yollar
2. **Siz**, Lisans Üretici'de kodu yapıştırırsınız
   - Araç **Bilgisayar kimliği: XXXXXXXXXXXX** gösterir
   - Bu, okulun ekranında yazan kimlikle **aynı olmalı** — farklıysa yanlış kod gelmiş
3. Okul adını yazın → **Dosya üret** → kaydedin
4. Dosyayı okula yollayın
5. Okul **"Lisans dosyası yükle (.lic)"** ile seçer

> Dosya **yalnızca o bilgisayarda** çalışır. Kopyalasalar, USB'ye atsalar bile
> başka makinede geçersizdir.

### 6. İlk giriş

Lisans geçilince **giriş ekranı** gelir:

> *İlk yönetici için tek kullanımlık güvenli parola oluşturuldu ve otomatik dolduruldu.*

**Kullanıcı adı ve parola kutuları kendiliğinden doludur.** Sadece **Giriş**'e basın.

Sonra: **Ayarlar → Kullanıcılar** → kendi parolanızı belirleyin.

### 7. Okulun bilgilerini girin

| Ekran | Ne yapılır |
|---|---|
| **Ayarlar → Genel** | Okul adı (raporların başlığına yazılır) |
| **Tanımlar → Sınıflar / Şubeler** | Sınıf ve şubeler |
| **Tanımlar → Öğün türleri** | Öğle, kahvaltı… ve **ücretleri** |
| **Öğrenciler** | Tek tek ekleyin veya **Öğrenci Aktar** ile Excel'den toplu alın |
| **Cihazlar** | Turnike / kart okuyucu varsa tanımlayın |

### 8. Yedeklemeyi ayarlayın

**Ayarlar → Yedekleme** — okula bunu **mutlaka gösterin**.

Veriler şurada: `%LOCALAPPDATA%\YemekhanePro`

---

## Sonraki açılışlar

Lisans ekranı **bir daha gelmez**. Okul her açılışta yalnızca
**kullanıcı adı + şifre** girer.

---

## Sorun çıkarsa

| Belirti | Çözüm |
|---|---|
| Kurulum çubuğu doldu, hiçbir şey olmadı | `.msi` çift tıklandı. `.exe` olanı kullanın |
| "Lisans anahtarı geçersiz" | Kurulumu ürettiğiniz sır ile anahtarı ürettiğiniz sır **aynı olmalı** |
| "Bu lisans başka bir bilgisayara ait" | Dosya başka makine için üretilmiş. Yeni makine kodu isteyin |
| "Kod okunamadı" | Makine kodu eksik kopyalanmış. **Tamamını** yeniden isteyin |
| Giriş ekranı parolayı doldurmadı | Bilgisayarda eski veritabanı var. Önceki parolayla girin |
| Windows SmartScreen uyarısı | Kurulum imzalı değil. **Ek bilgi → Yine de çalıştır** |

## Okul bilgisayar değiştirirse

1. Eski bilgisayarda **Ayarlar → Yedekleme** → yedek alın
2. Yeni bilgisayara kurun
3. **Yeni makine kodu** isteyip yeni lisans dosyası üretin
   (anahtar kullandıysanız aynı anahtar da çalışır)
4. Yedeği geri yükleyin

## Güncelleme

Yeni sürümün `.exe`'sini doğrudan çalıştırın — eskisi otomatik kaldırılır,
**veriler ve lisans yerinde kalır**. Kaldırmanıza gerek yok.
