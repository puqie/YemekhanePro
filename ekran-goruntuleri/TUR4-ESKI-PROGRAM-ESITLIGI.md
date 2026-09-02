# Tur 4 — Eski programda olup bizde olmayan her şey

**İstek:** "Şu an kullandıkları mevcut programda olup bizde olmayan hiçbir şey kalmasın."

Eski program (WinForms) ekran görüntüleri tek tek kod tabanıyla karşılaştırıldı. Eksik çıkan yedi başlık beş paralel iş akışında tamamlandı; hepsi ana dalda.

## Eklenenler

| Eski programdaki ekran | Bizdeki karşılığı | Notlar |
|---|---|---|
| **Öğün Tanım** (Öğün Adı, Başlama/Bitiş Saati, **Ücret TL**) | Tanımlar → Öğünler | Ücret kuruş hassasiyetinde saklanır; `250,50` ve `250.50` aynı sonucu verir |
| **Departman / Bölüm / Sınıf / Görev Tanım** (dört ayrı form) | Tanımlar → dört sekme | Kullanılan tanım silinmez; kaç öğrencide kullanıldığı mesajda söylenir |
| **Sicil Kartı** (TC, doğum tarihi, departman, bölüm, sınıf, görev, adres, PI ID, parmak izi, fotoğraf, "+" ile hızlı tanım) | Öğrenciler → Öğrenci Kartı çekmecesi | Fotoğraf yükle/kaldır; dosya türü içeriğinden doğrulanır, 2 MB sınırı |
| **Yemek Hakkı Yetkilendirme → Öğün Bedeli / Toplam** | Hızlı Hakediş çekmecesi | "Öğün bedeli: ₺250,00" ve önizlemede "Toplam bedel" |
| **Sms Sistemi Tanımları → üç otomatik kural** (hak bitiyor uyarısı + saat + gün eşiği, gelir girişinde yetkiliye SMS, kart yenileme) | Ayarlar → SMS → Otomatik SMS | Arka plan servisi her gün belirlenen saatte çalışır; "Şimdi gönder" düğmesi var |
| **Raporlar → Sicil Listesi** | Rapor Merkezi → Sicil Listesi (ilk sıra) | CSV/Excel/PDF; Öğrenciler ekranındaki "Dışa Aktar" buraya getirir |
| **Günlük Giriş Listesi Detaylı** | Günlük Geçiş raporu | Bölüm ve Görev sütunları eklendi (Kolonlar menüsünden açılır) |
| **Sicil Aktar → cihaz sicil listesi** | Kart Yükleme Durumu → cihazdaki kartlar | Arama, sayfalama, satır bazlı "Yeniden yükle" |
| **Tl Bakiye Yükleme** | Kasa → Bakiye Yükle + Öğrenciler → Bakiye sekmesi | Hakediş yoksa öğün ücreti bakiyeden düşer; gelir iptalinde iade edilir |

Zaten bizde olanlar: Kurum Tanım (Ayarlar → Okul), Cihaz Tanım, Gelir Türü Tanım, Sicil Aktar (dosyadan), Günlük Giriş ve Günlük Yemek raporları.

## Yol boyunca bulunan gerçek hatalar

Bu turda yalnızca yeni özellik yazılmadı; mevcut kodda kullanıcının yaşayacağı hatalar da çıktı ve düzeltildi:

- **Öğrenci formunda tek alan yazınca ekranda görünmüyordu** (alanlar değişiklik bildirimi yapmıyordu).
- **Silinmiş/pasif bir tanıma bağlı öğrencinin sınıfı ilk kaydetmede siliniyordu**; artık "(tanımsız)" olarak korunuyor.
- **"Aktif" filtresi pasifleri de getiriyordu** (`ACTIVE` metni `INACTIVE` içinde geçtiği için).
- **Kart yenileme SMS'i sessizce kayboluyordu** (SQLite'ın desteklemediği bir sıralama, hata yutuluyordu).
- **Reddedilen ayar kaydı bir daha gönderilemiyordu**; hata mesajı ekranda asılı kalıyordu.
- **Rapor sütunları eziliyordu**: yıldız sütun (AD SOYAD) kalan alanın tamamını yutup diğerlerini alt sınıra sıkıştırıyor, metinler kesiliyordu. Üstelik **ezilmiş genişlik diske kaydediliyor**, bozulma sonraki açılışlarda kalıcı oluyordu.
- **Sicil listesi ters sıralanıyordu** (varsayılan azalan sıralama).
- Bildirim testi yük altında rastgele düşüyordu (bekleme koşulu eksikti).

## Doğrulama

Taze tohumlu veritabanına karşı **65 canlı yolculuk** ve tam birim paketi. Yolculukların üçü, farklı yolculukların yazdığı veriyi sabit varsaydığı için koşu sırasına bağımlıydı (otomatik SMS akışı bir öğrencinin kartını değiştiriyor); ilgili yolculuklar artık öğrencinin o anki aktif kartını okuyor ve rapor türünü sırayla değil türüyle seçiyor.

Ekran görüntüleri: `tur4-*.png`.

## Yapılmayan tek şey

**Cihazın kendi belleğindeki sicil listesini doğrudan okuma** (eski programdaki "Cihaz Sicil Listesi" düğmesi). Bu, cihaz sürücüsüne bağlı ve sizin üzerinde çalıştığınız turnike dosyalarınıza dokunmayı gerektiriyor; o yüzden yapılmadı. Bizdeki ekran sunucudaki kart yükleme durumunu gösteriyor ve bunu ekranda açıkça yazıyor.
