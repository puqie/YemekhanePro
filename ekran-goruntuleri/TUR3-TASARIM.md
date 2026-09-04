# Tur 3 — Karartma, tarih/metin kutuları ve simge düzeltmeleri

**İstek:** Çekmece açılınca karartma sayfanın tamamını kaplamıyor; tarih ve metin kutuları orantısız; simgeler garip yerlerde.

## Bulgu ve kök neden

| Sorun | Kök neden | Çözüm |
|---|---|---|
| Karartma yan menüyü, sayfa başlığını ve 22/18 px kenar boşluklarını kaplamıyor | Çekmece ve onay pencereleri her ekranın **kendi Grid'inde** durur; karartma Border'ı o Grid'i kaplar, MainWindow'daki yan menü sütunu dışarıda kalır | `Controls/WindowScrim.cs`: karartma Border'ına `WindowScrim.Extend="True"` verilince aynı fırça pencerenin **AdornerLayer**'ına tüm pencere boyunca çizilir; Border'ın kendi alanı delik bırakılır (renk iki kez binmez, panel görünür kalır). Adorner'a tıklama aynı olayı Border'a iletir: dışına tıklayınca kapanma her yerde çalışır |
| Ekran görüntülerinin altında 40 px beyaz şerit ("karartma sayfayı kaplamıyor" gibi görünüyordu) | Harness `Window`'u çiziyordu; `ActualHeight` başlık çubuğunu sayıyor | Harness istemci alanını (`AdornerDecorator`) `VisualBrush` ile çizer |
| Takvim gün paneli karartmasız düz bir Border; "Kapat" başlığın üstünde | Diğer ekranlardan farklı, elle yazılmış panel | `controls:Drawer` oldu: aynı başlık/Kapat/Esc/karartma |
| **"Kapat"a bir kez basınca çekmece bir daha açılmıyordu** (gizli hata) | `Drawer.Close()` `IsOpen = false` yazıyordu; IsOpen ViewModel'in özel setter'lı özelliğine **tek yönlü** bağlı, yerel değer bağlamayı koparıyor | `SetCurrentValue` ile bağlama korunur; mutasyonla kanıtlandı (`WindowScrimTests.KapatTekYonluBaglamayiKoparmaz`) |
| Tarih kutusunda metin üst kenara yapışık, küçük takvim simgesi sağ alt köşede, gri iç kenarlık | Windows'un varsayılan `DatePicker`/`DatePickerTextBox` şablonu; `DatePickerTextBox` implicit TextBox stilini almaz | `DesignSystem.xaml`'de tam şablon: metin dikeyde ortalı, takvim simgesi (Segoe MDL2) sağda ortalı ve tıklanabilir, aynı köşe/kenarlık |
| Açılır kutular gri degrade, metin kutularıyla yan yana orantısız | Varsayılan Windows ComboBox şablonu | Beyaz zemin, aynı kenarlık, sağda ortalı ok; açılan liste marka renginde vurgu |
| Gelir Ekle: tarih 180 px + saat 80 px, sağda boşluk | Sabit genişlikler | Tarih esner, saat 96 px |
| Hızlı Hakediş: tarih aralığı 150+150 dar | Sabit genişlikler | İki eşit sütun |
| Öğrenci formu: "Not" kutusu ortadan kesik, kart kutusu etiketsiz, sekme içeriğine 105 px kalıyor | 5*:3* satır payı 900 px'te yetmiyor | NO/Ad/Soyad tek satır, form satırı `Auto` (en çok 250), kalan sekmelere; "Yeni kart no" etiketi |
| Raporlar filtre etiketleri 10 px BÜYÜK HARF, diğer ekranlardan farklı | Ekrana özel stil | Ortak `Label` stili, cümle düzeni |
| Dashboard başlığı beyaz 64 px şeritte 18 px, diğer ekranlardan farklı; renkli emoji zil | Ayrı başlık düzeni | PageShell ile aynı başlık stili; tek renk sistem simgesi |
| SMS Gönder'de "Manuel seçim"de iki BOŞ açılır kutu (Sınıf/Grup) | Hedef türüne bakılmadan her zaman görünüyordu | Yalnızca ilgili hedef türünde görünür |
| 429 (dakikada 5 rapor/aktarım isteği) "İstek işlenemedi, tekrar deneyin" diyordu | Genel yedek mesaj | "Kısa sürede çok fazla istek… bir dakika bekleyin" |
| Sunucu 500'leri hiçbir yere yazılmıyordu | `ApiExceptionHandler` istisnayı yutuyordu | 500'ler `LogError` ile günlüğe |

## Canlı doğrulama

Taze tohumlu veritabanına karşı **58 canlı yolculuk** (tümü yeşil, son koşu) + birim testleri. Bu turda harness'te bulunan üç sorun da düzeltildi:
- Yolculuklar tek kalıcı UI iş parçacığında koşar (aksi halde ikinci yolculuktan itibaren SignalR "Bağlanıyor"da asılı kalıyordu; ürün hatası değil).
- Test sürecinin SQLite havuzu WAL dosyasını kilitliyor, geri yükleme 500 dönüyordu (`Pooling=False`).
- Sicil Aktar yolculuğu hız sınırı penceresini bekler.

Ekran görüntüleri: `tur3-*.png` (çekmece/karartma: `tur3-cash-13-iptal-cekmece`, `tur3-ent-10-cekmece-manuel`, `tur3-cal-11-tatil-formu`, `tur3-bulk-03-adim3-tarih`, `tur3-students-33-kart-oku`; tarih/kutu: `tur3-reports-DailyCash-eylul`, `tur3-sms-11-gecmis`, `tur3-settings-06-yedekleme`; öğrenci formu: `tur3-students-50-tasarim`; dashboard: `tur3-dashboard-01`).

## Dokunulmayan (sizin dosyanız)

`DevicesView.xaml:47` cihaz düzenleyici karartması hâlâ yalnızca sayfayı kaplar; aynı tek satır (`controls:WindowScrim.Extend="True"`, `xmlns:controls` ile) yeter. `GET api/devices/{id}/logs` 500'ü günlükte artık görünür (`DateTimeOffset` ORDER BY).
