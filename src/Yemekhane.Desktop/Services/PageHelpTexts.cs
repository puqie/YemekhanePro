namespace Yemekhane.Desktop.Services;

/// <summary>
/// F1 yardim penceresinin ust bolumunde gosterilen, sayfaya ozel kullanim
/// notlari. Anahtar <see cref="ShellRoutes"/> sabitleriyle ayni (MainWindow
/// zaten BaseRoute ile rotayi bu kimliklerden birine indirgiyor). Rota
/// burada yoksa MainWindow yalnizca klavye kisayollarini gosterir.
/// </summary>
public static class PageHelpTexts
{
    public static readonly IReadOnlyDictionary<string, PageHelp> ByRoute = new Dictionary<string, PageHelp>
    {
        [ShellRoutes.Dashboard] = new("Genel Bakış",
        [
            "Bugünün özetini gösterir: aktif öğrenci, hak sahibi, kullanılan/kalan hak sayıları.",
            "Canlı Geçişler tablosu turnikeden son 20 geçişi anlık gösterir.",
            "Cihaz Durumu kartından turnikelerin çevrimiçi/çevrimdışı/hatalı sayısını görürsünüz; Yönet ile Cihazlar ekranına geçersiniz.",
            "Hızlı İşlemler satırındaki düğmeler en sık kullanılan ekranlara doğrudan götürür.",
            "Geçişler ve cihaz durumu canlı bağlantıyla anında gelir; bağlantı kopunca \"Çevrimdışı\" rozeti çıkar, o zaman Yenile ile tazeleyin.",
        ]),
        [ShellRoutes.DailyTracking] = new("Günlük Takip",
        [
            "Seçilen tarihte hangi öğrencinin yemek yiyip yemediğini, hangi öğünde okutma yaptığını listeler.",
            "Reddedilen geçişin gerekçesi Neden sütunundadır (örn. öğün saati dışı okutma).",
            "Ara kutusu ve Karar/Öğün/Cihaz/Sınıf filtreleriyle daraltabilirsiniz; en eski kayıtlar için alttaki \"Daha eski kayıtları yükle\" düğmesini kullanın.",
        ]),
        [ShellRoutes.Students] = new("Öğrenciler",
        [
            "Öğrenci arama, yeni öğrenci ekleme ve kart atama/değiştirme işlemleri burada yapılır.",
            "Tek arama kutusu ad, soyad, öğrenci no ve kart no'da birden arar (en az 2 karakter); Sınıf/Şube/Bölüm/Durum ile daraltabilirsiniz.",
            "Yeni kaydedilen bir öğrenciye kart atamak için kartı burada okutun; ekran hem ilk atamayı hem değişimi aynı akışla yönetir.",
            "Öğrenci numarası, kart no ve veli adı zorunlu değildir; yalnızca ad ve soyad gerekir. Kartsız öğrenci (anasınıfı gibi) kaydedilir, sayımlarda ve hakedişlerde görünür.",
            "Formda kullanmadığınız alanları \"Alanlar\" düğmesinden kalıcı olarak gizleyebilirsiniz; gizlenen alanın kayıtlı değeri silinmez.",
            "F2 arama kutusuna odaklanır. F3 \"Kartla Öğrenci Bul\" penceresini açar; bunun için kart yetkisi (cards.manage) ve bağlı bir kart okuyucu gerekir.",
            "Bir öğrenciye tıklayınca detay panelinde hakediş, ödeme ve geçiş geçmişi görünür.",
        ]),
        [ShellRoutes.Cash] = new("Kasa",
        [
            "Nakit/bakiye tahsilatı, bakiye yükleme ve iptal (void) işlemleri burada yapılır.",
            "Tahsilat öncesi öğrenci doğrulaması gerekir; bu adım öğrenci kayıtlarını okuma izni ister — kasiyer rolüne bu izin verilmemişse doğrulama sessizce başarısız olur.",
            "Bir işlemi iptal etmek (Void) tutarı geri almaz, yalnızca kaydı geçersiz işaretler ve gerekçesini tutar.",
            "Gelir Ekle'de öğrenciyi ad, soyad, numara ya da kartla arayıp listeden seçebilirsiniz; numara ezberlemeniz gerekmez.",
            "Kantin, bağış gibi gelirler için \"Öğrenciye bağlı olmayan gelir\" kutusunu işaretleyin; öğrenci seçilmez.",
            "Anasınıfı Ücretleri sekmesinde sınıfa toplam/taksitli, aylık ya da günlük ücret tanımlanır; taksitler otomatik oluşur.",
            "Öğrenci Ekstresi sekmesi bir öğrencinin ödemelerini, bakiyesini, taksit borcunu ve yemek kullanımını tarih aralığıyla verir; PDF olarak kaydedilir.",
        ]),
        [ShellRoutes.Entitlements] = new("Yemek Hakedişleri",
        [
            "Öğrencilere/sınıflara toplu veya tekil yemek hakkı tanımlama ekranıdır.",
            "Toplu İşlem Sihirbazı ile birden fazla öğrenciye/sınıfa aynı anda hak verebilir, geçmişini görebilirsiniz.",
            "Hızlı Hakediş'te öğrenciyi ad, sınıf ya da numarayla arayıp listeden seçersiniz; aradaki liste her öğrenciyi tek satır gösterir.",
            "Öğünün bedeli varsa \"Ücreti kasaya gelir olarak işle\" ile tutar öğrenci başına kasaya yazılır; hak iptal edilirse tahsilat da iptal edilir.",
            "\"Veliye bilgi SMS'i gönder\" ile veliye tarih aralığı ve tutar bildirilir.",
            "Bir hakkı iptal etmeden önce onay istenir; iptal edilen kayıt geri alınamaz, yeniden hak tanımlamanız gerekir.",
            "Aynı öğrenciye aynı gün ve aynı öğün için tekrar hak verirseniz ikinci bir hak açılmaz; var olan kayıt güncellenir.",
            "Genel Bakış'ta \"HAK SAHİBİ\" o günkü öğrenci sayısı, \"HAKEDİŞ\" ise hak adedidir: bir öğrenciye iki öğün verilirse hak sahibi 1, hakediş 2 görünür.",
            "Hak verdikten sonra liste kendiliğinden tazelenir ve arama kutusu temizlenir; yeni satırı görmek için tekrar hak vermeniz gerekmez.",
        ]),
        [ShellRoutes.HolidayTransfer] = new("Takvim / Tatil",
        [
            "Resmi tatil ve okul tatillerini tanımladığınız, kullanılmayan hakların sonraki güne devredildiği ekrandır.",
            "Ekran kendiliğinden güncellenir: sayfaya her geçişte ve açıkken belirli aralıklarla tazelenir, Yenile'ye basmanız gerekmez.",
            "\"Sınıf türü\" süzgeci mutfağa verilecek sayıyı belirler: varsayılan İlkokul (anasınıfı hariç, sınıfı girilmemiş öğrenciler dahil).",
            "Çok günlü bir tatilde her günün hakkı kendi sırasına göre ayrı bir sonraki boş iş gününe devredilir; hepsi tek güne yığılmaz.",
            "Belirli bir güne devir istiyorsanız Hak davranışı olarak \"Belirli bir tarihe aktar\" seçip Hedef tarih girin; bu durumda yığılma sizin isteğinizdir.",
            "Tatil, kaydedildiğinde hakları kendisi değiştirmez; seçtiğiniz davranış \"Hakediş etkilerini toplu uygula\" ile uygulanır.",
            "Çok günlü tatil her gün için ayrı satır oluşturur; Tatiller bloğundan tek günü ya da tüm aralığı silebilirsiniz.",
        ]),
        [ShellRoutes.StudentImport] = new("Sicil Aktar",
        [
            "Excel/CSV dosyasından toplu öğrenci içe aktarma ekranıdır.",
            "Yıl sonu sıfırlamasından sonra buradan yüklenen öğrenciler otomatik olarak yeniden aktif hale gelir.",
            "Dosyada NO, Kart No, Ad, Soyad, Sınıf ve Veli telefonu sütunları bulunmalı (.xlsx veya .csv, en fazla 10 MB). Hatalı satırlar için \"Hata Raporunu İndir\" düğmesi vardır.",
        ]),
        [ShellRoutes.Definitions] = new("Tanımlar",
        [
            "Sınıf, öğün türü ve diğer temel tanımların yönetildiği ekrandır.",
            "Sınıf, şube, bölüm ve görev sekmelerinde F2 seçili tanımı yeniden adlandırır; Öğünler sekmesinde F2 öğünü düzenlemeye açar.",
            "Öğünler pasife alınır; sınıf/şube/bölüm/görev ise yalnızca silinir ve öğrencide kullanılan tanım silinemez (önce öğrencileri taşıyın).",
        ]),
        [ShellRoutes.Devices] = new("Cihazlar / Turnikeler",
        [
            "Turnike ve kart okuyucu cihazlarının bağlantı durumunu, günlüklerini ve ayarlarını yönetir.",
            "Bir cihaz turnike komutunu hiç alamazsa (bağlantı koptu, kapasite yok) yemek hakkı otomatik iade edilir; belirsiz durumlarda (yazarken bağlantı koptu gibi) inceleme için kayıt bırakılır ve iade edilmez — bu kayıtları takip edin.",
            "Cihaz satırındaki Loglar düğmesi o cihazın günlüğünü açar; uygulama geneli kayıtlar Ayarlar > Loglar'dadır.",
        ]),
        [ShellRoutes.DeviceCards] = new("Kart Yükleme Durumu",
        [
            "Öğrenci kartlarının hangi cihaza yüklendiğini, hangisinin bekleyen/başarısız olduğunu gösterir.",
            "Bir kart birden çok cihaza ayrı ayrı yüklenir; her cihaz-kart çifti kendi durumunu taşır.",
        ]),
        [ShellRoutes.Sms] = new("SMS Merkezi",
        [
            "Üç sekme: Gönder (alıcıları önizleyip SMS'leri kuyruğa alır), Şablonlar (hazır metinler, kurulumla birlikte örnekler gelir) ve Geçmiş.",
            "Geçmişte 'Kaynak' sütunu SMS'in elle mi yoksa Ayarlar'daki otomatik kurallardan mı gönderildiğini gösterir.",
            "SMS sağlayıcısı Ayarlar → SMS sekmesinde yapılandırılmadan gönderim yapılamaz.",
        ]),
        [ShellRoutes.Reports] = new("Raporlar",
        [
            "Gelir, kullanım ve sicil listesi gibi raporları filtreleyip PDF/Excel olarak dışa aktarabilirsiniz.",
            "Ctrl+P ile PDF, Ctrl+E ile Excel dışa aktarımı yalnızca bu ekranda, rapor hazırlandıktan sonra ve dışa aktarma izniyle çalışır.",
            "Bölüm, Görev, Veli ve TC Kimlik sütunları varsayılan gizlidir; Kolonlar düğmesinden açılır.",
            "Yıl sonu sıfırlamasından sonra da geçmiş tahsilat ve gelir raporlarına erişebilirsiniz; bu veriler silinmez.",
        ]),
        [ShellRoutes.Settings] = new("Ayarlar",
        [
            "Okul bilgileri, yönetim ekranı bağlantıları, SMS sağlayıcısı, yedekleme, yıl sonu sıfırlama, senkronizasyon ve günlükler burada yönetilir.",
            "SMS sekmesindeki \"SMS gönderimi açık\" kutusu tüm gönderimi durdurur; kapalıyken mesajlar kuyrukta bekler ve açtığınızda gönderilir.",
            "Yardım / AI Kılavuzu sekmesinde, uygulamanın tamamını bir yapay zekaya anlatan hazır bir metin bulabilir, kopyalayıp kullanabilirsiniz.",
            "Değişikliklerin çoğu Kaydet'e basılınca hemen geçerli olur; yedekleme/senkronizasyon zamanlaması, kuyruk için SMS sağlayıcı değişikliği ve geri yükleme uygulama yeniden başlatılınca uygulanır.",
        ]),
    };
}

/// <summary>Bir sayfanin F1 yardim penceresinde gosterilecek basligi ve madde listesi.</summary>
public sealed record PageHelp(string Title, IReadOnlyList<string> Bullets);
