# Tur 5 — Ek denetim: "halen eksikler var gibi"

Dört bağımsız denetim (tanımlar/sicil kartı, raporlar, kasa/SMS, arayüz tasarımı) kod tabanını
eski program ekran görüntülerine karşı yeniden taradı. Çıkan bulgular tek tek doğrulandı;
spekülatif olanlar elendi.

## Giderilenler

| Bulgu | Kullanıcı ne yaşıyordu | Durum |
|---|---|---|
| **Veli hiçbir ekrandan eklenemiyordu** | Sunucuda uçlar vardı, masaüstü yalnızca listeliyordu. Veli tek yoldan — CSV içe aktarımından, orada da sabit "Veli" adıyla — girebiliyordu. Otomatik SMS veli telefonuna dayandığı için elle açılan öğrenciye **hiçbir zaman SMS gidemiyordu**. Üstelik sağ panelde salt okunur "Veli Tel" etiketi göründüğü için alan bir yerde sanılıyordu. | Öğrenci Kartı çekmecesine "Veli adı" ve "Veli telefonu" eklendi |
| **Bakiyeden yapılan harcama hiçbir raporda görünmüyordu** | Düşüm `StudentBalanceEntry` olarak yazılır, `IncomeTransaction` olarak değil; Gelir ve Günlük Kasa yalnızca ikincisini okuduğu için yüklemeler görünüp harcamalar görünmüyordu. "Bu ay bakiyeden ne kadar yemek yenildi", "kimin bakiyesi eksiye düştü" sorulamıyordu. | Yeni **Bakiye Hareketleri** raporu (CSV/Excel/PDF) |
| **Rapor başlığı kaydedilen okul adını yok sayıyordu** | Ayarlar → Okul'a gerçek okul adı yazılıp kaydediliyor, "kaydedildi" mesajı alınıyor, ama PDF/Excel başlığında hâlâ sabit "Okul Yemekhanesi" yazıyordu. Veliye/müdürlüğe giden her rapor yanlış kurum adıyla çıkıyordu. | Ad artık her üretimde veritabanından canlı okunur; yoksa yapılandırmadaki ad yedek |
| **Dosyada bakiye nedenleri İngilizce kalıyordu** | Rapor ekranda "Bakiyeden düşüldü" yazarken aynı raporun PDF/Excel/CSV çıktısında ham `BalanceUsed`; reddedilen geçişte `InsufficientBalance` belgeye basılıyordu. | `ReportText` sözlüğü tamamlandı |
| **"Karar" filtresi serbest metindi** | Sonuç sütunu "İzin Verildi" yazarken filtreye İngilizce `ALLOW` kodunu ezberleyip yazmak gerekiyordu; Türkçesini yazan hiç sonuç alamıyordu. | Açılır kutu (aynı ekrandaki "Durum" filtresiyle aynı desen) |
| **Kasa filtresinde ters tarih "Çevrimdışı" gösteriyordu** | Başlangıç > bitiş girilince başlıkta turuncu "Çevrimdışı" rozeti beliriyor, "alınamadı" yazıyordu; kullanıcı API/ağ arızası sanıp bağlantıyı kurcalıyordu. | Doğrulama isteğin önüne alındı |
| **Arayüzde kalan İngilizce** | Yan menüde "Dashboard", sayfa başlığında "Operasyon Dashboard". | "Genel Bakış" |

## Doğrulama

- **1833 birim testi** yeşil (üç ardışık tam koşu).
- Bakiye raporu ve okul adı düzeltmesi **canlı API'ye karşı** ayrıca doğrulandı: gerçek bir
  yükleme kaydı oluşturulup rapor CSV/Excel/PDF olarak alındı; Excel başlığında kaydedilen
  "Şehit Öğretmen Anadolu Lisesi" adı, satırda "Yükleme" (ham `TopUp` değil) göründü.
- Yeni testler: veli ekleme/güncelleme/kaldırma/değişmediyse istek göndermeme/eksik bilgi
  uyarısı (5), okul adı önceliği ve okunamazsa rapor üretiminin sürmesi (4), dosyada bakiye
  nedenlerinin Türkçeleşmesi (1).

## Bilerek yapılmayanlar

**Cihazlar ekranındaki dört bulgu** — düzenleyici karartmasının yan menüyü kaplamaması, log
panelinin karartmasız açılması, yön açılır kutusunun ham `Entry/Exit/Bidirectional` göstermesi
ve log listesinde `registration`/`status` değerlerinin çevrilmemesi. Hepsi
`DevicesView.xaml` / `DevicesViewModel.cs` içinde; bunlar sizin üzerinde çalıştığınız,
commit edilmemiş turnike dosyaları. Dokunulmadı.

**Yazdırma ve baskı önizleme** — eski programdaki "Print" düğmesinin doğrudan karşılığı yok;
PDF kaydedilip açılarak yazdırılıyor. Ayrıca `Ctrl+P` kısayolu yazdırma değil PDF kaydetme
yapıyor, bu da beklentiyi yanlış yönlendiriyor. İkisi de ayrı bir iş; istenirse yapılır.

**Öğrenci fotoğrafları yedeklemeye dahil değil** — arşiv yalnızca veritabanı, ayarlar ve
manifest taşır; `photos/` klasörü geri yüklemede kaybolur. Arşiv biçimi, doğrulayıcı
(`Entries.Count != 3`), manifest kuralı ve `CurrentFormatVersion` birlikte değişmeli.

**Cinsiyet, öğrencinin kendi telefonu, kurum vergi bilgileri** — eski kartta vardı, bizde yok;
şema değişikliği gerektirir ve işlevsel bir akışı engellemiyorlar.
