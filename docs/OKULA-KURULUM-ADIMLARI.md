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
> Özel anahtar yalnızca **sizin Windows hesabınızda** çözülür. Bilgisayar değiştirirseniz yeni çift üretip yeni kurulum dağıtmanız gerekir.

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
