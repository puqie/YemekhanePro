# Tur 6 — Kısayollar, komut bağlantıları ve tasarım denetimi

**İstek:** "Kısayollar vs her şey çalışıyor mu, tasarımsal hata var mı, komple bak."

İki bağımsız denetim: (1) ekranda görünen ama çalışmayan komut/bağlantı var mı, (2) görsel
tutarlılık ve kesilen içerik. Kısayolları ayrıca elle inceledim.

## Çalışmayan şeyler (giderildi)

| Bulgu | Kullanıcı ne yaşıyordu |
|---|---|
| **Sayfalama düğmeleri gri kalıyordu** (Öğrenciler, Kasa) | `AsyncCommand`, WPF'in `RequerySuggested` mekanizmasına bağlanmaz; `Page`/`TotalCount` değiştiğinde `Refresh()` çağrılmadığı için düğmeler ilk çizildikleri halde donuyordu. Açılışta toplam 0 olduğu için "Sonraki" pasif çiziliyor, liste gelince de pasif kalıyordu. **200+ öğrencide liste ikinci sayfadan ötesine erişilemiyordu.** Hakedişler ekranı bunu baştan beri doğru yapıyormuş; fark yalnızca iki satırlık kablolamaymış. |
| **F2 Tanımlar'da yanlış iş yapıyordu** | Pencere kısayolu tabloya ulaşmadan tuşu yutuyordu: F2 seçili tanımı yeniden adlandırmak yerine kullanıcıyı Öğrenciler ekranına atıyordu. |
| **Escape iki çekmeceyi görmüyordu** | Kasa → Bakiye Yükle ve Tanımlar → yeniden adlandırma açıkken Escape hiçbir şey yapmıyordu. Kapatma komutları zaten varmış, sadece kabuğa bağlanmamışlar. |
| **F1 yardımında ham rota adı** | Tanımlar ekranında "Etkin: definitions" yazıyordu; Genel Bakış da eski "Dashboard" adıyla kalmıştı. |

## Tasarım hataları (giderildi)

| Bulgu | Kullanıcı ne görüyordu |
|---|---|
| **Sekiz çok satırlı kutu metni sarmıyordu** | Tasarım sistemindeki varsayılan dikey hizalama "orta". `TextWrapping` yazılmayan kutularda metin sarmıyor, tek satır halinde sağa kayıyor ve kutunun **dikey ortasında** yüzüyordu. 500 karakter yazılabildiği halde bir satırı görünüyor, kaydırma çubuğu da olmadığı için yazılan geri okunamıyordu. En kötüsü Kasa **"İptal nedeni"**: zorunlu ve denetim izine giren alan. (Kasa ×3, Hakedişler, Toplu İşlem ×2, SMS ×2) |
| **Üç tablo ekrana sığmıyordu** | Günlük Takip 1293px, Hakedişler 1224px, Kasa 1208px — kullanılabilir alan ~1180px. Yatay kaydırma çubuğu çıkıyor, son sütun ekran dışında kalıyordu. |
| **Kesilen sütunlar** | Günlük Takip'te "Sınıf" 90px sabitti; "Anaokulu A" hücre dolgusuyla tam sınırdaydı, bir karakter uzunu kesiliyordu. Hakedişler'de "DURUM" 76px'e "Tamamlandı" sığmıyordu. |
| **Karışık başlık düzeni** | SMS öğrenci tablosunda `Seç`, `No`, `Öğrenci` ile `SINIF`, `ŞUBE` yan yanaydı. |
| **Yedi düğme bitişik çiziliyordu** | `Action` stili taşımadıkları için aralarında boşluk yoktu (SMS şablon araç çubuğu, Ayarlar'da iki çift). |
| **Ctrl+P yanıltıcıydı** | İpucu "komutu hazır" diyordu; kullanıcı yazıcı bekleyip "Farklı Kaydet" penceresiyle karşılaşıyordu. Ne yaptığı açıkça yazıldı. |
| Sicil Aktar önizleme tablosunda erişilebilirlik adı yoktu | Ekran okuyucu tabloyu adlandırmıyordu. |

## Bunun tekrar olmaması için

Üç yeni koruma testi eklendi ve **hepsi mutasyonla doğrulandı** (düzeltmeyi geri alınca düşüyorlar):
- Çok satırlı her `TextBox` sarma + üstten hizalama taşımalı — tüm View'ları tarar.
- Gezilebilir her rotanın Türkçe adı olmalı.
- Sayfalama düğmeleri etkinleştiklerini WPF'e bildirmeli (Öğrenciler + Kasa).

Bir testin kendisi de bozuktu: yazdığım tarayıcı `"<TextBox\b..."` kullanıyordu; normal C# dizesinde
`\b` kelime sınırı değil **backspace karakteri** olduğu için hiçbir şey eşleşmiyor, test her zaman
geçiyordu. Mutasyon denemesi bunu yakaladı; düzeltilince gizli kalmış **dört ihlal daha** ortaya çıktı.

Ayrıca bildirim testi tam koşuda rastgele düşüyordu ("Collection was modified"): bekleme koşulu,
ViewModel listeyi güncellerken üzerinde LINQ çalıştırıyordu. Kod değil testin kendi yarışıydı.

## Temiz çıkanlar

- **~150 `Command` bağlaması**: hepsi gerçek bir komuta gidiyor; ölü düğme yok.
- **Tüm binding yolları**, `Click` işleyicileri, `StaticResource` başvuruları, `ElementName`
  referansları: eksik yok. (Proje zaten 13 ekranı tarayan bir `BindingIntegrityTests` taşıyor.)
- **On kısayolun onu da** uçtan uca bağlı; yazarken güvenli olmayanlar (Ctrl+P/Ctrl+E metin
  kutusunda, Enter çok satırlıda) doğru şekilde bastırılıyor.
- Renkler: hiçbir View'da doğrudan hex yok, hepsi temadan geliyor.

## Doğrulama

**1854 birim testi** yeşil (21'i bu turda eklendi). İki test tam koşuda yük altında ara sıra
düşüyor, tek başlarına ve tekrar koşuda geçiyorlar: `SF300AdapterTests` (zaman aşımı ölçen bir
test, sizin turnike alanınızda) ve düzelttiğim bildirim testi.

## Dokunulmayanlar

Cihazlar ekranındaki bulgular (karartma yan menüyü kaplamıyor, log paneli karartmasız, yön kutusu
ham `Entry/Exit` gösteriyor) yine kapsam dışı bırakıldı: `DevicesView.xaml` ve `DevicesViewModel.cs`
sizin commit edilmemiş turnike çalışmanız.
