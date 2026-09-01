# WPF Arayüz Yeniden Tasarımı

**Tarih:** 2026-09-01
**Durum:** Onaylandı, uygulanmayı bekliyor
**Kapsam:** `src/Yemekhane.Desktop` — kabuk, tema ve 12 ekran

---

## 1. Amaç

Masaüstü arayüzü hem görsel tutarlılık hem kullanım hızı açısından yeniden
kurmak. Kullanıcı geri bildirimi: "Arayüzü çok beğenmedim ve kullanışlı
gelmedi."

Kullanıcı profili: **tek kişi, bütün işleri yapıyor** (okul memuru). Vardiya
veya rol ayrımı yok. Aynı kişi gün içinde öğrenci kaydı, öğün atama ve
tahsilat yapıyor.

En sık kullanılan üç ekran (kullanıcı beyanı):

1. Sicil Kartı (Öğrenciler)
2. Öğün Atama (Yemek Hakedişleri)
3. Gelir (Kasa)

---

## 2. Teşhis

Mevcut arayüzün sorunları, koda dayalı olarak:

### 2.1 Tema tek kaynak değil

`Themes/DesignSystem.xaml` (542 satır) iyi yazılmış: WCAG kontrast notları,
4'ün katı ölçü sistemi, virtualization ayarları mevcut. Ancak **13 dosya
kendi renk ve stillerini yeniden tanımlıyor.**

`MainWindow.xaml` içinde `Ink`, `Muted`, `Line`, `Accent`, `Panel`,
`SectionTitle`, `QuietButton` yerel olarak tanımlı. `StudentsView.xaml`
tema dosyasını merge ediyor, ardından `Field`, `Action`, `Label`
stillerini üzerine yazıyor — yani merge işlemi etkisiz.

Sonuç: tek tasarım sistemi, 13 ayrı gerçeklik. Ekranlar birbirine yakın
ama hiçbiri aynı değil.

### 2.2 Ekran iskeleti tekrarlanıyor

Her view şu sekiz parçayı elle kuruyor: başlık, alt başlık, çevrimdışı
rozeti, araç çubuğu, yükleniyor göstergesi, boş-liste yazısı, hata satırı,
sayfalama.

Tutarsızlık örnekleri:

- Yükleniyor göstergesi `StudentsView`'de ızgaranın içinde ortalanmış,
  `CashView`'de tüm sayfayı kaplayan yarı saydam katman.
- `CashView` alt bandında üç ayrı hizalama var: solda hata, ortada yıkıcı
  düğme, sağda sayfalama.

### 2.3 Çekmeceler standart değil

15 çekmece, **6 farklı genişlik**: 390, 430, 440, 470, 650 piksel.

`StudentsView` içinde dört çekmece var (`IsQuickDetailOpen`,
`IsDetailOpen`, `IsFormOpen`, `IsCardWorkflowOpen`). Hiçbiri diğerini
kapatmıyor; üst üste binme `Panel.ZIndex` sırasına bırakılmış.
`IsFormOpen` zaten `IsDetailOpen`'ın içine gömülü — çekmece içinde çekmece.

Hiçbir çekmecede Esc ile kapatma veya odak yönetimi yok.

### 2.4 Yıkıcı eylemler yanlış yerde

`CashView`: "Seçili İşlemi İptal Et" düğmesi sayfanın **ortasında**,
sayfalama düğmelerinin yanında, nötr stille duruyor.

`SettingsView`: "Geri Yükle" düğmesi `Background="#A4403A"` ile elle
boyanmış — tasarım sisteminde `Destructive` stili varken kullanılmamış.

### 2.5 Ayırt edicilik eksik

`CashViewModel.VoidConfirmationText` şunu üretiyor:

    "₺250,00 • ALİ YILDIZ"

Bir işlem iptal edilirken yalnızca tutar ve ad soyad gösteriliyor. Gerçek
veride aynı isimden birden fazla kişi var (ekran görüntülerinde doğrulandı:
ADA KATIRCI / ADA HAŞLAMACI / ADA SÖYLEMEZ; ALİ AVCI / ALİ AYDIN /
ALİ TEKİNGÜNDÜZ / ALİ BAŞKAYA).

Buna karşılık `LookupStudentText` doğru davranıyor:

    "5371 • FATİH SİDAL • Kart: 8352094"

Yani kod içinde bile tutarsızlık var. Veritabanı tarafı sağlam
(`StudentNo` ve `CardNumber` indeksli); sorun tamamen arayüzde.

### 2.6 Keşfedilebilirlik

Kullanıcı yedekleme özelliğinin eklenmesini istedi. Özellik **zaten
mevcut**: Ayarlar → Yedekleme sekmesinde, `BackupsController` ile tam
çalışır halde. Sahibi tarafından bulunamaması keşfedilebilirlik sorununun
kanıtı.

### 2.7 Menü ağırlıklandırılmamış

12 menü öğesi; aynı punto, aynı renk (`#D6DBE0`), aynı dolgu (`11,10`),
ikonsuz, gruplanmamış, mantıksız sırada (Kart Yükleme Durumu ile SMS
Merkezi yan yana). Her düğmenin stili tek tek elle yazılmış — 12 kez
tekrarlanan aynı 8 özellik.

---

## 3. Tasarım

### 3.1 Kabuk ve gezinme

**Sol menü — üç gruba ayrılır.** Sıralama iş akışına göre, alfabetik değil:

| Grup | Öğeler |
|---|---|
| Günlük iş | Panel · Günlük Takip · Öğrenciler · Kasa |
| Tanımlar | Yemek Hakedişleri · Takvim/Tatil · Sicil Aktar |
| Sistem | Cihazlar · Kart Durumu · SMS · Raporlar · Ayarlar |

Gruplar arası ince ayraç ve küçük grup başlığı. Göz 12 satır yerine 3 blok
tarar.

Menü öğeleri tek bir `Style` ile kurulur (`Tag` bazlı). Seçili öğe sol
kenarında turuncu şerit ve parlak metinle işaretlenir — mevcut arka plan
rengi (`#2F3B49`) koyu zeminde zayıf sinyal.

**Üst bant.** Tam genişlikte şerit:

- Solda arama kutusu (Ctrl+K odaklanır) — hem öğrenci hem komut arar
- Sağda çevrimdışı rozeti, bildirim sayacı, kullanıcı adı

Çevrimdışı rozeti bugün her view'de ayrı çiziliyor; kabuğa taşınır.

**Komut paleti (Ctrl+K).** Yazdıkça filtreleyen liste. "kasa" → Kasa'ya
gider, "gelir" → Gelir Ekle açar, "1234" → o numaralı öğrenciyi bulur.
Her komutun yanında varsa kısayolu görünür; palet kısayolları öğretir.

Kullanım sayacı yerel olarak tutulur. Bir ay sonra en sık üç işlem üst
banda kalıcı düğme olarak terfi ettirilir — bugün tahminle verilmeyen
karar, sonra veriyle verilir.

F1 kısayol ekranı korunur, paletten de erişilir.

**Dokunulmaz:** `MainWindow.xaml.cs`, `ShellNavigationService`,
`ShortcutCommandRouter`, tüm `Navigate*Command`'lar.

### 3.2 Tema tekilleştirme

13 dosyadaki yerel `SolidColorBrush` ve `Style` tanımları silinir.
`DesignSystem.xaml` tek kaynak olur.

### 3.3 Ortak sayfa iskeleti — `PageShell`

Yeniden kullanılabilir kontrol. Bölgeleri:

- **Başlık:** başlık + alt başlık solda, eylemler sağda
- **Filtre:** isteğe bağlı, kart içinde
- **İçerik:** ızgara veya form
- **Alt bant:** solda hata, sağda sayfalama — her ekranda aynı yerde

Yükleniyor, boş liste ve hata durumları tek yerde tanımlanır.

View'ler yaklaşık yarı yarıya kısalır.

### 3.4 Veri ızgaraları

Yerel `DataGrid` ayar tekrarları silinir (`RowHeight`,
`HorizontalGridLinesBrush`, virtualization) — tema zaten tanımlıyor.

Düzeltmeler:

- **Satır yüksekliği 34** (tema değeri). View'ler 29–30'a düşürüyor;
  uzun süre tabloya bakan için sıkışık, tıklama hedefi küçük.
- **Durum sütunları rozet olur.** Şu an `DURUM` sütunu `IsActive` alanını
  ham basıyor, ekranda "True"/"False" yazıyor. Yeşil "Aktif" / gri "Pasif"
  rozetine çevrilir. Aynısı `BUGÜNKÜ HAK` ve `İPTAL` için.
- **Sütun genişlikleri önem sırasına göre** yeniden ölçülür.
- `NO` ve `KART NO` ilk iki sütun olarak kalır (ayırt edici oldukları için).

Rozet dönüşümleri **converter ile** yapılır; ViewModel'lere dokunulmaz.

### 3.5 Yıkıcı eylemler

- `CashView` "Seçili İşlemi İptal Et": sayfa ortasından ızgara üstü araç
  çubuğuna taşınır, `Destructive` stili uygulanır. Yanındaki "Düzenleme ve
  silme desteklenmez." notu alt banda iner (eylem değil, not).
- `SettingsView` "Geri Yükle": elle boyama kaldırılır, `Destructive`
  stiline çekilir.
- Onay kutuları (`AddConfirmed`, `VoidConfirmed`) korunur ve
  vurgulanır: uyarı zeminine alınır, onaylanmadan düğme pasif kalır.

### 3.6 Çekmeceler — `Drawer` kontrolü

**Üç standart ölçü:**

| Ölçü | Genişlik | Kullanım |
|---|---|---|
| Dar | 400 px | hızlı bakış, onay, tek alanlı işlem |
| Geniş | 640 px | form ve detay |
| Modal | ortada | yalnızca bloke edici (kart okuma) |

Ortak kontrol şunları garanti eder:

- Esc kapatır
- Arkada karartma katmanı; dışına tıklayınca kapanır
- **Aynı anda tek çekmece** açık
- Odak yönetimi: açılınca ilk alana, kapanınca geldiği yere
- Kapat düğmesi her zaman sağ üstte

### 3.7 Formlar

- Zorunlu alanlar işaretlenir
- Hata **alanın altında** görünür, formun dibinde değil
- Kaydet düğmesi formun altında sabit; uzun formda kaydırınca kaybolmaz

### 3.8 Üç ana ekran — liste + form yan yana

Bu üç ekranda çekmece **kullanılmaz**. Eski uygulamanın doğru yaptığı şey
form ve listeyi aynı anda göstermesiydi; mevcut WPF bunu çekmeceye gömerek
yavaşlatmış.

#### Sicil Kartı (Öğrenciler)

Liste solda kalıcı, form sağda kalıcı. Satır seçilince form dolar.

**Kalan alanlar:** No, Ad, Soyad, Sınıf, Şube, Kart No, Veli Tel, Not,
Durum

**Çıkan alanlar:** TC, Doğum Tarihi, Adres, Departman, Görev, PID, Resim,
Parmak İzi

Veritabanı alanları (`NationalId`, `BirthDate`, `DepartmentId`, `JobId`,
`Pid`, `Address`, `PhotoPath`, `FingerprintId`) **silinmez** — yalnızca
arayüzde gösterilmez. Silmek migration ve veri kaybı riski getirir.

Not: `StudentsView` formunda hâlihazırda yalnızca 6 alan var (No, Ad,
Soyad, TC, Adres, Not). Sadeleştirmenin yarısı zaten yapılmış; kalan iş
TC ve Adres'i çıkarmak.

Liste sütun başlıklarının üstünde sütun bazlı arama kutuları (No, Ad,
Soyad, Kart No) — eski uygulamadan alınan iyi fikir.

Kart okuma (`IsCardWorkflowOpen`) **modal olarak kalır**, çünkü cihazdan
olay beklerken kullanıcının başka iş yapmaması gerekir. Diğer üç çekmece
(`IsQuickDetailOpen`, `IsDetailOpen`, `IsFormOpen`) kalkar; yerlerini
kalıcı sağ form alır.

#### Öğün Atama (Yemek Hakedişleri)

Solda checkbox'lı çoklu seçim listesi + "Hepsini seç". Sağda atanacak öğün
formu (öğün, adet, gün, başlangıç tarihi, Cmt/Paz dahil).

Seçili öğrenci sayısı sürekli görünür.

Mevcut "Hızlı Hakediş" çekmecesindeki *"öğrenci kimliklerini virgülle
girin"* alanı kaldırılır — kullanışsız.

**"Etkileri Önizle" korunur.** Bu, mevcut uygulamanın eskisinden üstün
olduğu yer: 200 öğrenciye yanlış atama yapmayı engelliyor.

#### Gelir (Kasa)

Öğrenci listeden seçilir; "Doğrula" düğmesine basmaya gerek kalmaz.
Seçilince ad ve kart otomatik dolar.

Alt tarafta günün işlemleri ve toplamı her zaman görünür.

#### Diğer 9 ekran

`PageShell` + `Drawer` deseni uygulanır. Günde birkaç kez kullanıldıkları
için liste+form ayrımına gerek yok.

### 3.9 Ayırt edicilik

**Kural: öğrenci hiçbir yerde yalnız adıyla gösterilmez.**

**1. Listelerde:** `NO` ve `KART NO` ilk iki sütun; `SINIF`/`ŞUBE`
ad-soyadın hemen yanında.

**2. Tek satırlık gösterimlerde** standart kimlik biçimi, tek converter ile:

    FATİH SİDAL · No 5371 · 6E · Kart 8352094

Uygulanacağı yerler: kasa iptal onayı, hakediş iptal onayı, SMS alıcı
listesi, arama sonuçları, komut paleti.

`VoidConfirmationText` artık `"₺250,00 · ALİ YILDIZ · No 6970 · 7B ·
Kart 5931590"` üretir.

**3. Aynı isim uyarısı.** Öğrenci seçildiğinde aynı ad-soyada sahip başkası
varsa arayüz uyarır ve adayları listeler:

    ⚠ Aynı isimde 3 kişi var — No ve sınıfı kontrol edin
       ADA KATIRCI · 6E · Kart 8713484   ← seçili
       ADA HAŞLAMACI · 7B · Kart 8699170
       ADA SÖYLEMEZ · 5A · Kart 8340659

Görüneceği yerler: gelir girişi, hakediş atama, SMS gönderimi — yani para
ve hak söz konusu olan her yer.

**4. Arama sonuçları** sınıfa göre gruplanır; aynı isimler yan yana gelir.

Önce converter ile denenir. Yetmezse ViewModel eklemesi kullanıcıya
sorulur.

### 3.10 Yedekleme

**Ayarlar'da kalır** (kullanıcı kararı). Yeri değişmez, görünürlüğü artar:

- **Panelde tek satır:** "Son yedek: 3 gün önce" — eskiyse turuncu,
  tıklanınca Ayarlar'ın Yedekleme sekmesini açar.
- **Sekme içi düzenlenir:** şu an her şey tek satır XAML'e sıkışmış.
  Üstte zamanlama ayarları, altta ayraçla ayrılmış elle işlemler.

Backend'e **dokunulmaz.** `BackupsController`'a yeni uç (örneğin
`/backups/preview`) eklenmez; geri yükleme onayında yedeğin tarihi
gösterilir, kayıt sayıları gösterilmez. Gerekçe: çalışma ağacında yarım
kalmış API işleri var, çakışma riski.

---

## 4. Dokunulmayacak alanlar

| Alan | Gerekçe |
|---|---|
| Tüm ViewModel'ler | Testler `DataContext` üzerinden çalışıyor; binding adları korunur |
| `ShellNavigationService`, `ShortcutCommandRouter` | Test edilmiş gezinme mantığı |
| `Yemekhane.Api` | Çalışma ağacında yarım kalmış işler var |
| `DevicesView`, `DeviceCardsView` yerleşimi | `DevicesView.xaml` çalışma ağacında kaydedilmemiş değişiklik taşıyor; yalnızca temaya dahil edilir, yapıları korunur |
| Veritabanı şeması | Kaldırılan alanlar arayüzden çıkar, şemadan çıkmaz |

ViewModel'ler sağlam durumda (en büyüğü 338 satır, hepsi tek sorumluluklu).
XAML değişip binding adları korunursa iş mantığı risk almaz.

---

## 5. Uygulama sırası

Aşamalı ilerlenir. Her aşamada build + test + inceleme yapılır.

**Aşama 1 — Altyapı**
Tema tekilleştirme, kabuk (menü grupları, üst bant, komut paleti),
`PageShell`, `Drawer` kontrolü.

En riskli aşama: 13 dosyadaki yerel stiller silinince beklenmedik görünüm
bozulması çıkabilir. 12 ekran yazıldıktan sonra değil, burada görülmeli.

**Aşama 2 — Üç ana ekran**
Sicil Kartı, Öğün Atama, Gelir. Liste + form yan yana düzeni.
Ayırt edicilik converter'ı.

**Aşama 3 — Kalan 9 ekran**
`PageShell` + `Drawer` uygulanır. Cihaz ekranları yalnızca temaya alınır.

---

## 6. Başarı ölçütü

- Tüm ekranlar tek tema kaynağını kullanır; yerel renk/stil tanımı kalmaz
- Aynı işlev her ekranda aynı yerde ve aynı görünümde
- Yıkıcı eylemler `Destructive` stili taşır ve beklenen yerde durur
- Hiçbir onay ekranı öğrenciyi yalnız adıyla göstermez
- Çekmeceler Esc ile kapanır, aynı anda yalnız biri açıktır
- Mevcut testler geçmeye devam eder (ViewModel'lere dokunulmadığı için)
