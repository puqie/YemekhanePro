# Lisans Sunucusu — Kurulum ve Kullanım

Sunucuya koyacağınız parça: **`src/Yemekhane.LicenseServer`**. Süresiz ve yıllık lisans
satar, aktivasyonu tek makineye bağlar, iptal ettiğinizde saha kurulumunu kapatır.

---

## 1. İki sır üretin

**İmza sırrı** — masaüstü kurulumuna gömülen sır ile **birebir aynı** olmalı. Farklı olursa
sunucunun sattığı lisansı masaüstü "kurcalanmış" sayar ve uygulama hiç açılmaz.

```powershell
# İmza sırrı (kurulum üretirken de AYNISI kullanılır)
[Convert]::ToBase64String((1..48 | % { Get-Random -Max 256 }))

# Yönetici belirteci (en az 24 karakter)
[Convert]::ToBase64String((1..32 | % { Get-Random -Max 256 }))
```

İkisini de bir yere kaydedin. İmza sırrını **kaybederseniz** satılmış tüm lisanslar
doğrulanamaz hale gelir.

## 2. Sunucuyu yayımlayın

```powershell
dotnet publish src\Yemekhane.LicenseServer -c Release -o C:\lisans-sunucusu
```

Ortam değişkenleriyle çalıştırın (sırlar dosyaya yazılmaz):

```powershell
$env:Licensing__SigningSecret = '<imza sırrı>'
$env:Licensing__AdminToken    = '<yönetici belirteci>'
$env:Licensing__DataDirectory = 'C:\lisans-sunucusu\data'
$env:ASPNETCORE_URLS          = 'http://0.0.0.0:8080'
C:\lisans-sunucusu\Yemekhane.LicenseServer.exe
```

**HTTPS şart.** Lisans anahtarları ve yönetici belirteci düz HTTP'de ağda açık gider.
IIS, Nginx ya da Caddy'yi önüne ters vekil (reverse proxy) olarak koyup sertifika verin.

Windows'ta servis olarak: `sc.exe create YemekhaneLisans binPath= "C:\lisans-sunucusu\Yemekhane.LicenseServer.exe"`

## 3. Masaüstü kurulumunu aynı sırla üretin

```powershell
$env:YEMEKHANE_LICENSING_SECRET = '<AYNI imza sırrı>'
.\scripts\build-installer.ps1 -Version 1.1.0
```

Çıktı: `artifacts\installer\YemekhanePro-Setup-1.1.0.exe`

Masaüstünün sunucuyu bulması için `appsettings.json` içinde aktivasyon adresi
sunucunuzu göstermeli (`https://lisans.siteniz.com/`).

## 4. Yönetim ekranı

Tarayıcıdan sunucunun kök adresini açın: `https://lisans.siteniz.com/`

Yönetici belirtecinizi girin (yalnızca o tarayıcı sekmesinde tutulur, sunucuya
kaydedilmez). Ekrandan:

| İşlem | Ne yapar |
|---|---|
| **Lisans oluştur** | Süresiz veya 1/2/3/5 yıllık. Anahtar ekranda çıkar, müşteriye verirsiniz |
| **İptal et** | Bir sonraki doğrulamada saha kurulumu kapanır |
| **İptali geri al** | Yanlışlıkla iptal edilen lisansı geri açar |
| **+1 yıl** | Aboneliği uzatır. Süresi dolmamışsa mevcut bitişin üstüne ekler (erken yenileyen gün kaybetmez) |
| **Süresiz yap** | Yıllık lisansı süresize çevirir |
| **Makineyi çöz** | Müşteri bilgisayar değiştirdiğinde. Anahtar aynı kalır, yeni makinede aktive edilir |

Listede her lisansın **son görülme** zamanı ve doğrulama sayısı da görünür: sahada
gerçekten kullanılıp kullanılmadığını buradan anlarsınız.

## 5. Uçlar

**Müşteri tarafı (masaüstü çağırır, kimlik doğrulaması yok, dakikada 20 istek sınırı):**

| Uç | Yanıt |
|---|---|
| `POST /activate` | 200 başarılı · 404 anahtar yok · 409 başka makinede · 410 iptal/süresi dolmuş |
| `POST /validate` | 200 geçerli · 410 iptal |
| `GET /health` | Servis ayakta mı |

**Yönetim (hepsi `X-Admin-Token` başlığı ister):**

```
GET  /admin/licenses?search=...
POST /admin/licenses                        {customerName, edition, years|null, notes}
POST /admin/licenses/{key}/revoke           {reason}
POST /admin/licenses/{key}/restore
POST /admin/licenses/{key}/extend           {years}
POST /admin/licenses/{key}/perpetual
POST /admin/licenses/{key}/release-machine
```

## 6. Tasarım kararları

**Lisans tek makineye bağlanır.** İlk aktivasyonda makinenin donanım parmak izleri
kaydedilir; ikinci bir bilgisayar 409 alır. Aynı makine tekrar aktive edilebilir
(format, yeniden kurulum) — aksi halde müşteri her formatta size ulaşmak zorunda kalırdı.

**Donanım bilgisi ham saklanmaz.** Masaüstü parmak izlerini hash'leyip gönderir; sunucu
sızsa bile müşterilerin donanım kimliği ele geçmez.

**Ağ hatası ile iptal ayrıdır.** Sunucuya ulaşılamazsa masaüstü çevrimdışı toleransa
düşer, kilitlenmez. Yalnızca sunucu açıkça 410 dönerse lisans geçersizleşir — internet
kesintisi okulu kilitlemez, ama iptal de işe yarar.

**Süresi dolmuş lisansa "iptal" denmez.** Masaüstü bitiş tarihini kendi bilir ve
"aboneliğiniz bitti" der; 410 dönseydi müşteriye "satıcı iptal etti" yazardı.

**Bilinmeyen anahtar doğrulamada iptal sayılır.** Veritabanından silinen bir lisans
sahada sonsuza kadar çalışmaya devam etmemeli.

**Anahtarlar tahmin edilemez.** Sıralı numara verilseydi (YMK-0001, YMK-0002) müşteri
komşu numarayı deneyerek başkasının lisansını aktive edebilirdi. Karışan karakterler
(0/O, 1/I/L) alfabeden çıkarıldı: anahtar telefonda okunup elle yazılıyor.

## 7. Yedekleme

Tek dosya: `<DataDirectory>\licenses.db`. Bunu kaybederseniz kimin ne aldığını
bilemezsiniz. Günlük yedekleyin.
