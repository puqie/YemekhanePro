# Tur 8 — Derin İnceleme

Soru: *"Daha derin inceler misin?"*

Yüzey testleri geçiyordu. Bu tur **kimsenin bakmadığı yerlere** baktı: eşzamanlılık,
para hassasiyeti, yetki sızıntıları, veri kaybı, Türkçe metin işleme.

Yöntem: her iddia **ölçümle** doğrulandı. Kod okuyup "sağlam görünüyor" demek yerine,
korumayı kasten bozup testin yakalayıp yakalamadığına bakıldı.

---

## 1. GERÇEK AÇIK — veli telefonu izinsiz sızıyordu

**Etki:** `reports.export` izni olan bir kullanıcı, `students.sensitive.read` izni
**olmadan** tüm okulun veli telefonlarını Excel/CSV/PDF olarak indirebiliyordu.

**Kanıt** — aynı token, iki farklı uç:

| Uç | Dönen değer |
|---|---|
| `/api/students` | `•••••••4567` |
| `/api/reports/StudentList` | `+905551234567` |
| CSV indirmesi | `+905551234567` |

Yani izin kapısı kâğıt üzerinde duruyor ama rapor ucundan atlanabiliyordu.
`NationalId` korunuyordu, `ParentPhone` unutulmuştu.

**Düzeltme:** Maskeleme **iki çıkış noktasına birden** uygulandı —
`QueryAsync` (ekran) ve `StreamBatchesAsync` (PDF/Excel/CSV). Yalnızca birine
uygulamak kapıyı açık bırakırdı.

`null` yerine maskeleme seçildi: `/api/students` ile aynı görünüm (tutarlılık), ve
son dört hane personelin doğru veliyi ayırt etmesine yeter.

**Karşı kontrol eklendi:** hassas izinli kullanıcı telefonu **tam** görüyor —
düzeltme aşırıya kaçmadı.

---

## 2. YANLIŞ ALARM — "aynı para iki kez harcanabilir"

Bir inceleme, bakiye düşümünde `BEGIN IMMEDIATE` kullanılmadığını ve iki eşzamanlı
geçişin aynı parayı harcayabileceğini bildirdi. **Doğrulandı: yanlış.**

**Ölçüm 1 — SQLite davranışı:** Microsoft.Data.Sqlite `BeginTransaction()` çağrısında
varsayılan olarak yazma kilidi alıyor. İkinci transaction anında
`SQLite Error 5: database is locked` verdi.

**Ölçüm 2 — gerçek kodla yarış testi:** 20 eşzamanlı okutma, bakiye tam 1 öğüne yeter:

```
okutma=20  allow=1  düşüm=1  bakiye=0  ZİRVE_EŞZAMANLI=20
```

`ZİRVE_EŞZAMANLI=20` kritik: 20 istek gerçekten aynı anda içerideydi, sırayla
koşmadılar. Yarış oluştu ve koruma tuttu.

**Ölçüm 3 — korumanın yeri:** Bakiye yeterlilik kontrolü mutasyonla kaldırıldığında
bakiye **−127.500 kuruşa** düştü (18 bedava öğün). Yani koruma gerçekten orada ve
yeni testler onu kilitliyor.

**Kalıcı kazanım:** Ödemeli yol daha önce eşzamanlılık testi ile kapsanmıyordu
(yemek hakkı yolu kapsanıyordu). Artık 5 test var.

---

## 3. GEÇİŞ KARARI — 9 red dalının 9'u da korunuyor

Her red dalı tek tek devre dışı bırakılıp tam takım koşuldu:

| Dal | Sonuç |
|---|---|
| Kart tanımsız | YAKALANDI |
| Kart pasif | YAKALANDI |
| Öğrenci pasif | YAKALANDI |
| Cihaz pasif | YAKALANDI |
| Grup tatili | YAKALANDI |
| Öğrenci izinli | YAKALANDI |
| Öğün ücreti yok | YAKALANDI |
| Bakiye yetersiz | YAKALANDI |
| Öğün zaten kullanılmış | YAKALANDI |

Önceki turlarda "8 daldan 4'ü korumasız" diye kaydedilen boşluk **kapanmış**.

---

## 4. SAĞLAM ÇIKAN ALANLAR

Bunlar incelendi ve **doğru** bulundu — yanlış alarm vermemek için ayrıca yazıyorum:

| Alan | Bulgu |
|---|---|
| **Para hassasiyeti** | Kuruş cinsinden `long`; kayan nokta hatası yok. Taşma için 922 milyar yükleme gerekir, `MaxTopUpAmount` zaten önce devrede |
| **Yuvarlama** | `AwayFromZero` — tutarlı ve doğru |
| **Silme kuralları** | Para/hak kayıtları `Restrict`; öğrenci silme ucu **hiç yok**, yalnızca pasifleştirme (kasa geçmişi korunuyor) |
| **Yedekleme** | `File.Replace` atomik, rollback yolu var, bütünlük iki kez doğrulanıyor, hata halinde eski veritabanı geri konuyor |
| **Yedek temizliği** | Normal ve geri-yükleme-öncesi arşivler ayrı sayılıyor; taze arşiv korunuyor |
| **Kültür duyarlılığı** | Kültüre bağlı `ToUpper()`/`ToLower()` **hiç yok** — hepsi `Invariant`. Türkçe için doğru |

---

## 5. AÇIK KALAN — Türkçe aramada ASCII yazımı

**Ölçüm:** 423 öğrencinin **288'i (%68)** ASCII yazımla bulunamıyor.

```
'ALI SIMSEK'     aranırsa -> BULUNAMAZ   (kayıtlı: ALI ŞİMŞEK)
'AYSE HASLAMACI' aranırsa -> BULUNAMAZ   (kayıtlı: AYŞE HAŞLAMACI)
'ZEYNEP KOC'     aranırsa -> BULUNAMAZ   (kayıtlı: ZEYNEP KOÇ)
'HUSEYIN CETIN'  aranırsa -> BULUNAMAZ   (kayıtlı: HÜSEYİN ÇETİN)
```

`TurkishSearchText.Normalize` yalnızca `i/ı` çiftini birleştiriyor (bu bilinçli ve
belgeli). Ama `Ş Ç Ö Ü Ğ` olduğu gibi kalıyor — okul personeli hızlı yazarken
Türkçe karakter kullanmaz.

**Neden bu turda düzeltilmedi:** Düzeltme yeni bir migration ve geriye dönük
doldurma (backfill) gerektiriyor — `SearchName` yalnızca kayıt değiştiğinde
yenilendiği için mevcut kayıtlar eski değerde kalır. Migration üretmek,
commit edilmemiş turnike çalışmasındaki `ModelSnapshot` ile çakışabilir.
Karar kullanıcıya bırakıldı.

---

## 6. Sonuç

| Ölçüm | Sonuç |
|---|---|
| Birim testleri | **1965 / 1965** |
| Yeni test | 5 bakiye yarışı + 3 gizlilik + 1 karşı kontrol |
| Gerçek açık | 1 (veli telefonu sızıntısı) — **düzeltildi** |
| Yanlış alarm | 1 (bakiye yarışı) — ölçümle çürütüldü |
| Mutasyon | 11 mutasyon uygulandı, hepsi geri alındı |
