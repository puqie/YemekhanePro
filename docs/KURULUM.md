# YemekhanePro — Kurulum Kılavuzu

Sürüm 1.1.0 · Windows 10/11 (64 bit)

Bu kılavuz iki farklı kişiye hitap eder:

- **Bölüm A — Okul.** Programı okula kuran kişi. Tek bilgisayara kurulum, ilk giriş, lisans.
- **Bölüm B — Satıcı (siz).** Lisans sunucusunu kuran ve kurulum dosyasını üreten kişi.

Okula yalnızca **Bölüm A**'yı verin.

---

# Bölüm A — Okul için kurulum

## A1. Gereksinimler

| | |
|---|---|
| İşletim sistemi | Windows 10 (1809+) veya Windows 11, **64 bit** |
| Disk | 500 MB boş alan |
| Yetki | Kurulum sırasında **yönetici** hakkı (bir kez) |
| .NET | **Gerekmez.** Program kendi çalışma ortamını içinde taşır |
| İnternet | Yalnızca lisans etkinleştirme sırasında gerekir |

## A2. Kurulum

1. `YemekhanePro-Setup-1.1.0.exe` dosyasına çift tıklayın.
2. Windows "Bu uygulamanın cihazınızda değişiklik yapmasına izin verilsin mi?" diye sorar → **Evet**.
3. Lisans sözleşmesini kabul edin → **İleri**.
4. Kurulum klasörünü değiştirmeniz gerekmez → **İleri** → **Kur**.
5. **Son**.

Kurulum bittiğinde **masaüstünde** ve **Başlat menüsünde** YemekhanePro simgesi bulunur.

> Kurulum **tek dosyadır**: `YemekhanePro-Setup-1.1.0.exe`. Gereken her şey bu
> dosyanın içindedir.

## A3. İlk açılış — yönetici parolası

Programı ilk kez açtığınızda giriş ekranında şu yazar:

> *İlk yönetici için tek kullanımlık güvenli parola oluşturuldu ve otomatik dolduruldu.
> Girişten sonra parolanızı değiştirin.*

**Kullanıcı adı ve parola kutuları kendiliğinden doludur.** Yapmanız gereken tek şey
**Giriş** düğmesine basmaktır. Parolayı bir yere yazmanıza gerek yoktur — girer girmez
kendi parolanızı belirleyeceksiniz.

Giriş yaptıktan sonra: **Ayarlar → Kullanıcılar** ekranından kendi parolanızı değiştirin.

### "Parola dolu gelmedi" diyorsa

Ekranda şu yazıyorsa:

> *Bu bilgisayarda mevcut bir YemekhanePro veritabanı bulundu; kurulum parolası yalnızca
> ilk kurulumda oluşturulur.*

Bu bilgisayarda program **daha önce kurulmuş** demektir. Kurulum parolası yalnızca
**bomboş** bir veritabanında üretilir — aksi halde programı yeniden kuran herkes mevcut
okulun verisine yönetici olarak girebilirdi. Daha önce belirlediğiniz parolayla girin.

**Parolayı kimse bilmiyorsa:** aşağıdaki klasörü yedekleyip taşıyın, sonra programı yeniden
açın — boş veritabanı görüp yeni kurulum parolası üretecektir. **Bu, tüm verinizi sıfırlar;**
öğrenciler, geçmiş ve kasa kayıtları o klasörün içindedir.

```
%LOCALAPPDATA%\YemekhanePro
```

## A4. Lisans etkinleştirme

Program lisans ister. Satıcınızın verdiği anahtarı girin:

```
YMK-2026-XXXX-XXXX
```

Anahtar **kullanıldığı bilgisayara bağlanır**. Aynı bilgisayarda format atsanız, programı
kaldırıp yeniden kursanız da aynı anahtar çalışmaya devam eder.

**Bilgisayar değiştirecekseniz** satıcınıza haber verin; anahtarı makineden çözmesi gerekir.
Aksi halde yeni bilgisayarda *"Bu lisans başka bir bilgisayarda kullanılıyor"* hatası alırsınız.

**İnternet kesintisi programı kilitlemez.** Lisans sunucusuna ulaşılamadığında program
çevrimdışı toleransa düşer ve çalışmaya devam eder.

## A5. Verileriniz nerede

```
%LOCALAPPDATA%\YemekhanePro
```

Adres çubuğuna bunu yazarak açabilirsiniz. Öğrenciler, geçişler, kasa hareketleri —
hepsi buradadır.

**Yedekleme:** Program içinden **Ayarlar → Yedekleme** ekranını kullanın. Bu ekran
veritabanını tutarlı bir anda kopyalar; klasörü program açıkken elle kopyalamak
yarım kalmış bir dosya verebilir.

> Öğrenci fotoğrafları yedeğe **dahil değildir**. Fotoğraf kullanıyorsanız fotoğraf
> klasörünü ayrıca yedekleyin.

## A6. Kaldırma

**Ayarlar → Uygulamalar → YemekhanePro → Kaldır**

Kaldırma **verilerinizi silmez**; `%LOCALAPPDATA%\YemekhanePro` klasörü yerinde kalır.
Programı yeniden kurduğunuzda verileriniz geri gelir. Veriyi de silmek istiyorsanız
o klasörü elle silin.

### Yeni sürüme geçme

| | Konum | Kaldırmada |
|---|---|---|
| Program dosyaları | `C:\Program Files\YemekhanePro` | Silinir |
| **Verileriniz** | `%LOCALAPPDATA%\YemekhanePro` | **Dokunulmaz** |

Kurulum paketi veri klasörünü tanımıyor bile; kaldırmada sildiği tek şey Başlat
menüsü klasörüdür. Öğrenciler, geçişler, kasa geçmişi ve lisansınız yerinde kalır.

**En kolay yol: kaldırmayın.** Yeni sürümün `.exe` dosyasını doğrudan çalıştırın;
eski sürüm otomatik kaldırılır, veriniz korunur. Kaldırıp yeniden kurmayı tercih
ederseniz de sonuç aynıdır.

> Yine de her büyük güncellemeden önce **Ayarlar → Yedekleme**'den bir yedek almak
> iyi bir alışkanlıktır.

## A7. Sık karşılaşılan durumlar

| Belirti | Nedeni ve çözümü |
|---|---|
| "Daha yeni bir YemekhanePro sürümü zaten yüklü" | Yüklü sürüm daha yeni. Önce onu kaldırın |
| Program açılıyor, ekran bomboş | Veri klasörü yeni oluşmuştur; bu ilk kurulumda normaldir |
| Windows SmartScreen uyarısı | Kurulum dosyası imzalı değilse çıkar. **Ek bilgi → Yine de çalıştır** |
| Giriş ekranı "parola dolu gelmedi" diyor | Bilgisayarda eski veritabanı var (bkz. A3) |
| "Lisans başka bir bilgisayarda" | Anahtar başka makineye bağlı. Satıcıdan çözdürün (bkz. A4) |

---

# Bölüm B — Satıcı için kurulum

Bu bölüm **size** aittir; okula vermeyin.

## B1. İki sırrı üretin ve saklayın

**İmza sırrı** lisans sunucusu ile masaüstü kurulumunda **birebir aynı** olmalıdır.
Farklıysa sunucunun sattığı lisansı masaüstü "kurcalanmış" sayar ve program hiç açılmaz.

```powershell
# İmza sırrı
[Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Maximum 256 }))

# Yönetici belirteci
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }))
```

> **İmza sırrını kaybederseniz sattığınız tüm lisanslar doğrulanamaz hale gelir.**
> Parola yöneticinizde saklayın; depoya yazmayın.

## B2. Lisans sunucusunu yayımlayın

```powershell
dotnet publish src\Yemekhane.LicenseServer -c Release -o C:\lisans-sunucusu

$env:Licensing__SigningSecret = '<imza sırrı>'
$env:Licensing__AdminToken    = '<yönetici belirteci>'
$env:Licensing__DataDirectory = 'C:\lisans-sunucusu\data'
$env:ASPNETCORE_URLS          = 'http://0.0.0.0:8080'
C:\lisans-sunucusu\Yemekhane.LicenseServer.exe
```

**HTTPS şarttır.** Lisans anahtarları ve yönetici belirteci düz HTTP'de ağda açık gider.
IIS, Nginx veya Caddy'yi ters vekil olarak önüne koyup sertifika verin.

Ayrıntılar, uç listesi ve tasarım gerekçeleri: [lisans-sunucusu.md](lisans-sunucusu.md)

## B3. Kurulum dosyasını üretin

```powershell
$env:YEMEKHANE_LICENSING_SECRET = '<B1 ile AYNI imza sırrı>'
.\scripts\build-installer.ps1 -Version 1.1.0
```

Çıktı: `artifacts\installer\YemekhanePro-Setup-1.1.0.exe`

Betik derler, testleri koşar, yayımlar, MSI ve EXE üretir. Sır verilmezse **başlamadan
durur** — sırsız üretilen kurulum açılışta donacağı için bu kasıtlı bir kapıdır.

## B4. Aktivasyon adresini kendi sunucunuza çevirin

Yayımlanan `appsettings.json` içindeki varsayılan adres **örnek bir değerdir** ve
sizin sunucunuzu göstermez:

```json
"Licensing": { "ActivationUri": "https://lisans.yemekhanepro.com/api/" }
```

Kendi adresinizle değiştirin. **Sondaki `/api/` ekini kullanmayın** — sunucunun uçları
kökte durur (`/activate`, `/validate`), `/api/` eklerseniz istekler 404 döner ve
etkinleştirme hiç çalışmaz:

```json
"Licensing": { "ActivationUri": "https://lisans.siteniz.com/" }
```

Sondaki eğik çizgiyi **koruyun**: `.../lisans` yazarsanız `HttpClient` son bölümü atar.

Bu dosyayı `build-installer.ps1` çalıştırmadan **önce** düzenleyin ki kurulumun içine
doğru adres girsin.

## B5. Lisans satışı

Tarayıcıdan sunucunuzun kök adresini açın, yönetici belirtecinizi girin:

| İşlem | Ne yapar |
|---|---|
| **Lisans oluştur** | Süresiz veya 1/2/3/5 yıllık. Anahtar ekranda çıkar |
| **İptal et** | Bir sonraki doğrulamada okuldaki kurulum kapanır |
| **İptali geri al** | Yanlışlıkla iptal edileni geri açar |
| **+1 yıl** | Süresi dolmamışsa mevcut bitişin üstüne ekler |
| **Süresiz yap** | Yıllık lisansı süresize çevirir |
| **Makineyi çöz** | Okul bilgisayar değiştirdiğinde |

Listede her lisansın **son görülme** zamanı ve doğrulama sayısı görünür — sahada
gerçekten kullanılıp kullanılmadığını buradan anlarsınız.

## B6. Yedekleme

Lisans veritabanı tek dosyadır:

```
<DataDirectory>\licenses.db
```

Kaybederseniz kime ne sattığınızı bilemezsiniz. **Günlük yedekleyin.**
