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
            "Yenile ile veriler elle tazelenir; ekran ayrıca kendiliğinden periyodik güncellenir.",
        ]),
        [ShellRoutes.DailyTracking] = new("Günlük Takip",
        [
            "Seçilen tarihte hangi öğrencinin yemek yiyip yemediğini, hangi öğünde okutma yaptığını listeler.",
            "Öğün dışı (yanlış saatte) okutmalar ayrıca işaretlenir; bu bir hata değil, bilgi amaçlıdır.",
            "Filtrelerle sınıf veya öğrenciye göre daraltabilirsiniz.",
        ]),
        [ShellRoutes.Students] = new("Öğrenciler",
        [
            "Öğrenci arama, yeni öğrenci ekleme ve kart atama/değiştirme işlemleri burada yapılır.",
            "Yeni kaydedilen bir öğrenciye kart atamak için kartı burada okutun; ekran hem ilk atamayı hem değişimi aynı akışla yönetir.",
            "F3 ile kart okuma moduna, F2 ile hızlı arama kutusuna geçebilirsiniz (izniniz varsa).",
            "Bir öğrenciye tıklayınca detay panelinde hakediş, ödeme ve geçiş geçmişi görünür.",
        ]),
        [ShellRoutes.Cash] = new("Kasa",
        [
            "Nakit/bakiye tahsilatı, bakiye yükleme ve iptal (void) işlemleri burada yapılır.",
            "Tahsilat öncesi öğrenci doğrulaması gerekir; bu adım öğrenci kayıtlarını okuma izni ister — kasiyer rolüne bu izin verilmemişse doğrulama sessizce başarısız olur.",
            "Bir işlemi iptal etmek (Void) tutarı geri almaz, yalnızca kaydı geçersiz işaretler ve gerekçesini tutar.",
        ]),
        [ShellRoutes.Entitlements] = new("Yemek Hakedişleri",
        [
            "Öğrencilere/sınıflara toplu veya tekil yemek hakkı tanımlama ekranıdır.",
            "Toplu İşlem Sihirbazı ile birden fazla öğrenciye/sınıfa aynı anda hak verebilir, geçmişini görebilirsiniz.",
            "Bir hakkı iptal etmeden önce onay istenir; iptal edilen kayıt geri alınamaz, yeniden hak tanımlamanız gerekir.",
        ]),
        [ShellRoutes.HolidayTransfer] = new("Takvim / Tatil",
        [
            "Resmi tatil ve okul tatillerini tanımladığınız, kullanılmayan hakların sonraki güne devredildiği ekrandır.",
            "Çok günlü bir tatilde her günün hakkı kendi sırasına göre ayrı bir sonraki boş iş gününe devredilir; hepsi tek güne yığılmaz.",
            "Belirli bir tarihe devir istiyorsanız (ör. okul kararıyla) 'Tarih belirle' seçeneğini kullanın; bu durumda yığılma sizin isteğinizdir.",
        ]),
        [ShellRoutes.StudentImport] = new("Sicil Aktar",
        [
            "Excel/CSV dosyasından toplu öğrenci içe aktarma ekranıdır.",
            "Yıl sonu sıfırlamasından sonra buradan yüklenen öğrenciler otomatik olarak yeniden aktif hale gelir.",
            "İçe aktarmadan önce örnek şablonu indirip sütun sırasını bozmadan doldurmanız önerilir.",
        ]),
        [ShellRoutes.Definitions] = new("Tanımlar",
        [
            "Sınıf, öğün türü ve diğer temel tanımların yönetildiği ekrandır.",
            "F2 ile seçili tanımı yeniden adlandırabilirsiniz.",
            "Bir tanımı silmek yerine pasife almayı düşünün; geçmiş kayıtlar tanıma referans verebilir.",
        ]),
        [ShellRoutes.Devices] = new("Cihazlar / Turnikeler",
        [
            "Turnike ve kart okuyucu cihazlarının bağlantı durumunu, günlüklerini ve ayarlarını yönetir.",
            "Bir cihaz turnike komutunu hiç alamazsa (bağlantı koptu, kapasite yok) yemek hakkı otomatik iade edilir; belirsiz durumlarda (yazarken bağlantı koptu gibi) inceleme için kayıt bırakılır ve iade edilmez — bu kayıtları takip edin.",
            "Cihaz Günlükleri sekmesinden son hataları inceleyebilirsiniz.",
        ]),
        [ShellRoutes.DeviceCards] = new("Kart Yükleme Durumu",
        [
            "Öğrenci kartlarının hangi cihaza yüklendiğini, hangisinin bekleyen/başarısız olduğunu gösterir.",
            "Bir kart birden çok cihaza ayrı ayrı yüklenir; her cihaz-kart çifti kendi durumunu taşır.",
        ]),
        [ShellRoutes.Sms] = new("SMS Merkezi",
        [
            "Velilere gönderilen SMS'lerin geçmişini ve tekil gönderim ekranını içerir.",
            "Geçmişte 'Kaynak' sütunu SMS'in elle mi yoksa Ayarlar'daki otomatik kurallardan mı gönderildiğini gösterir.",
            "SMS sağlayıcısı Ayarlar → SMS sekmesinde yapılandırılmadan gönderim yapılamaz.",
        ]),
        [ShellRoutes.Reports] = new("Raporlar",
        [
            "Gelir, kullanım ve sicil listesi gibi raporları filtreleyip PDF/Excel olarak dışa aktarabilirsiniz.",
            "Ctrl+P ile PDF, Ctrl+E ile Excel dışa aktarımı kısayolla tetiklenir (izniniz varsa).",
            "Yıl sonu sıfırlamasından sonra da geçmiş tahsilat ve gelir raporlarına erişebilirsiniz; bu veriler silinmez.",
        ]),
        [ShellRoutes.Settings] = new("Ayarlar",
        [
            "Okul bilgileri, SMS sağlayıcısı, yedekleme, senkronizasyon, günlükler ve yıl sonu sıfırlama burada yönetilir.",
            "Yardım / AI Kılavuzu sekmesinde, uygulamanın tamamını bir yapay zekaya anlatan hazır bir metin bulabilir, kopyalayıp kullanabilirsiniz.",
            "Değişikliklerin çoğu Kaydet'e basılınca hemen geçerli olur; zamanlama (yedekleme/senkronizasyon sıklığı) gibi bazı ayarlar uygulama yeniden başlatılınca uygulanır.",
        ]),
    };
}

/// <summary>Bir sayfanin F1 yardim penceresinde gosterilecek basligi ve madde listesi.</summary>
public sealed record PageHelp(string Title, IReadOnlyList<string> Bullets);
