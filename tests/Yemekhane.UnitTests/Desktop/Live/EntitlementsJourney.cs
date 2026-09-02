using System.Windows.Controls;
using Xunit;
using Yemekhane.Application.Entitlements;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Yemek Hakedisleri ekrani: liste/filtre/sayfalama/ozet kartlari canli API ve
/// SQLite ile birebir mi; Hizli Hakedis, Iptal ve Toplu Islem akislari ucta uca.
/// </summary>
[Collection("UI")]
public class EntitlementsJourney
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static void Load(LiveUiHarness ui, MealEntitlementsViewModel vm, int page = 1)
    {
        Assert.True(LiveUiHarness.Wait(vm.LoadAsync(page), Timeout), "hakedis listesi zaman asimi");
        ui.Pump();
        Assert.False(vm.HasError, "liste hatasi: " + vm.ErrorMessage);
    }

    [Fact]
    public void ListeFiltrelerSayfalamaVeOzetVeritabaniylaBirebir() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("entitlements");
        var vm = ui.Entitlements;
        Assert.False(vm.HasError, vm.ErrorMessage);
        ui.Shot("ent-01-liste");

        // Varsayilan tarih araligi (bugun-7 .. bugun+7) SQLite ile birebir.
        var from = LiveDb.Date(vm.StartsOn!.Value); var to = LiveDb.Date(vm.EndsOn!.Value);
        var dbCount = LiveDb.Scalar("select count(*) from meal_entitlements where EntitlementDate between @p0 and @p1", from, to);
        var dbQuantity = LiveDb.Scalar("select coalesce(sum(Quantity),0) from meal_entitlements where EntitlementDate between @p0 and @p1", from, to);
        var dbConsumed = LiveDb.Scalar("select coalesce(sum(ConsumedQuantity),0) from meal_entitlements where EntitlementDate between @p0 and @p1", from, to);
        // Kalan: yalnizca AKTIF haklar (iptal/aktarilmis hakkin kullanilabilir kalani yoktur)
        var dbRemaining = LiveDb.Scalar("select coalesce(sum(Quantity-ConsumedQuantity),0) from meal_entitlements where EntitlementDate between @p0 and @p1 and Status='Active'", from, to);
        Assert.Equal(dbCount, vm.TotalCount);
        Assert.Equal(dbQuantity, vm.TotalQuantity);
        Assert.Equal(dbConsumed, vm.ConsumedQuantity);
        Assert.Equal(dbRemaining, vm.RemainingQuantity);
        Assert.Equal($"Sayfa 1 / {Math.Ceiling(dbCount / 50.0)} • {dbCount:N0} kayıt", vm.PageText);
        Assert.Equal(50, vm.Items.Count);
        ui.Note($"liste: {vm.PageText}; ozet {vm.TotalQuantity}/{vm.ConsumedQuantity}/{vm.RemainingQuantity} = DB");

        // Sayfalama: sonraki sayfa farkli kayitlar getirir, sayfa metni ilerler.
        var firstIds = vm.Items.Select(x => x.Id).ToHashSet();
        vm.NextPageCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Equal(2, vm.Page); Assert.StartsWith("Sayfa 2 /", vm.PageText);
        Assert.DoesNotContain(vm.Items, x => firstIds.Contains(x.Id));
        ui.Shot("ent-02-sayfa2");
        vm.PreviousPageCommand.Execute(null); ui.Delay(1500); ui.Pump(); Assert.Equal(1, vm.Page);

        // Ogrenci no filtresi
        vm.StudentNo = "5012"; Load(ui, vm);
        Assert.All(vm.Items, x => Assert.Equal("5012", x.StudentNo));
        var dbStudent = LiveDb.Scalar("select count(*) from meal_entitlements e join students s on s.Id=e.StudentId where s.student_no='5012' and e.EntitlementDate between @p0 and @p1", from, to);
        Assert.Equal(dbStudent, vm.TotalCount);
        ui.Shot("ent-03-filtre-no");
        vm.StudentNo = null;

        // Kart no filtresi
        vm.CardNumber = "8350012"; Load(ui, vm);
        Assert.All(vm.Items, x => Assert.Equal("8350012", x.CardNumber));
        Assert.Equal(dbStudent, vm.TotalCount);
        vm.CardNumber = null;

        // Ad soyad filtresi: ayni adli ogrenciler (uc ADA) listede no + sinif ile ayirt edilebilir.
        vm.StudentName = "ADA"; Load(ui, vm);
        Assert.All(vm.Items, x => Assert.Contains("ADA", x.StudentName, StringComparison.Ordinal));
        var dbAda = LiveDb.Scalar("select count(*) from meal_entitlements e join students s on s.Id=e.StudentId where (s.FirstName || ' ' || s.LastName) like '%ADA%' and e.EntitlementDate between @p0 and @p1", from, to);
        Assert.Equal(dbAda, vm.TotalCount);
        ui.Shot("ent-04-filtre-ad");
        vm.StudentName = null;

        // Sinif filtresi
        vm.ClassName = "7A"; Load(ui, vm);
        Assert.All(vm.Items, x => Assert.Equal("7A", x.ClassName));
        var db7a = LiveDb.Scalar("select count(*) from meal_entitlements e join students s on s.Id=e.StudentId join classes c on c.Id=s.ClassId where c.Name='7A' and e.EntitlementDate between @p0 and @p1", from, to);
        Assert.Equal(db7a, vm.TotalCount);
        vm.ClassName = null;

        // Ogun filtresi
        vm.SelectedMeal = vm.MealTypes.Single(x => x.Name == "Öğle Yemeği"); Load(ui, vm);
        Assert.Equal(dbCount, vm.TotalCount);
        vm.SelectedMeal = vm.MealTypes.Single(x => x.Name == "Kahvaltı"); Load(ui, vm);
        Assert.Equal(0, vm.TotalCount); Assert.True(vm.IsEmpty);
        ui.Shot("ent-05-bos-liste");
        vm.SelectedMeal = null;

        // Durum filtresi
        vm.Status = "Cancelled"; Load(ui, vm);
        Assert.Equal(LiveDb.Scalar("select count(*) from meal_entitlements where Status='Cancelled' and EntitlementDate between @p0 and @p1", from, to), vm.TotalCount);
        vm.Status = null; Load(ui, vm);
        Assert.Equal(dbCount, vm.TotalCount);

        // Tarih araligi daraltma
        vm.StartsOn = new DateTime(2026, 9, 2); vm.EndsOn = new DateTime(2026, 9, 2); Load(ui, vm);
        Assert.Equal(LiveDb.Scalar("select count(*) from meal_entitlements where EntitlementDate='2026-09-02'"), vm.TotalCount);
        Assert.All(vm.Items, x => Assert.Equal(new DateOnly(2026, 9, 2), x.Date));
        ui.Shot("ent-06-tek-gun");

        // Filtre alanlarinin gorunen etiketleri var mi?
        var labels = ui.FindAll<TextBlock>().Select(x => x.Text).ToHashSet();
        foreach (var label in new[] { "Başlangıç", "Bitiş", "Öğrenci no", "Kart no", "Ad soyad", "Sınıf", "Grup", "Öğün", "Durum" })
            Assert.Contains(label, labels);
    });

    [Fact]
    public void HizliHakedisSinifHedefiOnizlemeVeUygulama() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("entitlements");
        var vm = ui.Entitlements;
        vm.OpenGrantCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsGrantOpen);
        ui.Shot("ent-10-cekmece-manuel");

        // Sinif hedefi: 5A (27 ogrenci), 2026-09-21..2026-09-25 (5 is gunu, henuz hak yok)
        vm.TargetType = "Class"; ui.Pump();
        vm.GrantClass = vm.Classes.Single(x => x.Name == "5A");
        vm.GrantMeal = vm.MealTypes.Single(x => x.Name == "Öğle Yemeği");
        vm.GrantStartsOn = new DateTime(2026, 9, 21); vm.GrantEndsOn = new DateTime(2026, 9, 25);

        // Gecersiz gunluk adet: 0 ve 11 -> Turkce dogrulama mesaji, onizleme yok
        vm.Quantity = 0; vm.PreviewCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.False(vm.HasPreview);
        Assert.False(string.IsNullOrWhiteSpace(vm.PreviewMessage), "0 adet icin mesaj yok");
        ui.Note("adet=0 mesaji: " + vm.PreviewMessage);
        Assert.Contains("1-10", vm.PreviewMessage);
        ui.Shot("ent-11-adet-0");
        vm.Quantity = 11; vm.PreviewCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.False(vm.HasPreview); Assert.Contains("1-10", vm.PreviewMessage);
        // Harf girisi: int baglamada sessizce yutuluyordu; artik metin baglanir ve reddedilir.
        vm.QuantityText = "abc"; vm.PreviewCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.False(vm.HasPreview); Assert.Contains("1-10", vm.PreviewMessage);
        ui.Shot("ent-11b-adet-harf");

        vm.Quantity = 1; vm.PreviewCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.HasPreview, "onizleme yok: " + vm.PreviewMessage);
        var classCount = LiveDb.Scalar("select count(*) from students s join classes c on c.Id=s.ClassId where c.Name='5A' and s.IsActive=1");
        var existing = LiveDb.Scalar("select count(*) from meal_entitlements e join students s on s.Id=e.StudentId join classes c on c.Id=s.ClassId where c.Name='5A' and e.EntitlementDate between '2026-09-21' and '2026-09-25'");
        Assert.Equal(classCount, vm.Preview!.StudentCount);
        Assert.Equal(5, vm.Preview.DayCount);
        Assert.Equal(classCount * 5, vm.Preview.RightsCount);
        Assert.Equal(classCount * 5 - existing, vm.Preview.CreatedCount); Assert.Equal(existing, vm.Preview.UpdatedCount);
        ui.Note("onizleme: " + vm.PreviewText);
        ui.Shot("ent-12-onizleme-sinif");

        vm.ApplyCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.False(vm.IsGrantOpen, "uygulama sonrasi cekmece kapanmadi: " + vm.PreviewMessage);
        var created = LiveDb.Scalar("select count(*) from meal_entitlements e join students s on s.Id=e.StudentId join classes c on c.Id=s.ClassId where c.Name='5A' and e.EntitlementDate between '2026-09-21' and '2026-09-25'");
        Assert.Equal(classCount * 5, created);
        // Basari geri bildirimi ekranda: cekmece kapandi ama sonuc metni listede kaldi.
        Assert.True(vm.HasStatus, "uygulama sonrasi durum metni yok");
        Assert.Contains("hak oluşturuldu", vm.StatusMessage);
        // Filtre araligi verilen araligi kapsayacak sekilde genisledi; yeni satirlar listede.
        Assert.True(vm.EndsOn >= new DateTime(2026, 9, 25), "filtre araligi genislemedi");
        Assert.Contains(vm.Items, x => x.Date >= new DateOnly(2026, 9, 21) && x.ClassName == "5A");
        ui.Shot("ent-13-liste-yeni-haklar");

        // Listede yeni satirlar gorunur mu?
        vm.ClassName = "5A"; vm.StartsOn = new DateTime(2026, 9, 21); vm.EndsOn = new DateTime(2026, 9, 25); Load(ui, vm);
        Assert.Equal(classCount * 5, vm.TotalCount);
        Assert.Equal(classCount * 5, vm.TotalQuantity);

        // Ayni gune tekrar verme: cift kayit DEGIL, mevcut kayit guncellenir (adet 2 olur)
        vm.OpenGrantCommand.Execute(null); ui.Pump();
        vm.TargetType = "Class"; vm.GrantClass = vm.Classes.Single(x => x.Name == "5A");
        vm.GrantStartsOn = new DateTime(2026, 9, 21); vm.GrantEndsOn = new DateTime(2026, 9, 21); vm.Quantity = 2;
        vm.PreviewCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.HasPreview, vm.PreviewMessage);
        Assert.Equal(0, vm.Preview!.CreatedCount); Assert.Equal(classCount, vm.Preview.UpdatedCount);
        ui.Shot("ent-14-onizleme-guncelleme");
        vm.ApplyCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.False(vm.IsGrantOpen, vm.PreviewMessage);
        Assert.Equal(classCount, LiveDb.Scalar("select count(*) from meal_entitlements e join students s on s.Id=e.StudentId join classes c on c.Id=s.ClassId where c.Name='5A' and e.EntitlementDate='2026-09-21'"));
        Assert.Equal(classCount * 2, LiveDb.Scalar("select sum(Quantity) from meal_entitlements e join students s on s.Id=e.StudentId join classes c on c.Id=s.ClassId where c.Name='5A' and e.EntitlementDate='2026-09-21'"));
        Load(ui, vm);
        Assert.Equal(classCount * 5 + classCount, vm.TotalQuantity);
    });

    [Fact]
    public void HizliHakedisManuelHedefOgrenciNumarasiylaCalisir() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("entitlements");
        var vm = ui.Entitlements;
        vm.OpenGrantCommand.Execute(null); ui.Pump();
        Assert.Equal("Manual", vm.TargetType);
        vm.GrantMeal = vm.MealTypes.Single(x => x.Name == "Öğle Yemeği");
        vm.GrantStartsOn = new DateTime(2026, 9, 28); vm.GrantEndsOn = new DateTime(2026, 9, 28); vm.Quantity = 1;

        // Bos giris: kullaniciya ne gireceği soylenir
        vm.ManualStudentIds = ""; vm.PreviewCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.False(vm.HasPreview); Assert.Contains("numara", vm.PreviewMessage, StringComparison.OrdinalIgnoreCase);
        ui.Shot("ent-15-manuel-bos");

        // Bilinmeyen numara sessizce atlanmaz: adiyla reddedilir
        vm.ManualStudentIds = "5012, 5013, 5999"; vm.PreviewCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.False(vm.HasPreview, "5999 olmayan numara kabul edildi");
        Assert.Contains("5999", vm.PreviewMessage);
        ui.Note("bilinmeyen numara mesaji: " + vm.PreviewMessage);
        ui.Shot("ent-16-manuel-bilinmeyen-no");

        // Gecerli numaralar: 2 ogrenci x 1 gun
        vm.ManualStudentIds = "5012, 5013"; vm.PreviewCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.HasPreview, vm.PreviewMessage);
        Assert.Equal(2, vm.Preview!.StudentCount); Assert.Equal(1, vm.Preview.DayCount);
        ui.Shot("ent-17-manuel-onizleme");
        vm.ApplyCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.False(vm.IsGrantOpen, vm.PreviewMessage);
        Assert.Equal(2, LiveDb.Scalar("select count(*) from meal_entitlements e join students s on s.Id=e.StudentId where s.student_no in ('5012','5013') and e.EntitlementDate='2026-09-28' and e.Status='Active'"));

        // Kimlik (GUID) girisi de calismaya devam eder (derin baglanti yolu)
        var studentId = LiveDb.Rows("select Id from students where student_no='5014'")[0][0]!;
        vm.HandleRoute(Yemekhane.Desktop.Services.ShellRoutes.Entitlements + "/" + studentId.ToLowerInvariant()); ui.Pump();
        Assert.True(vm.IsGrantOpen);
        vm.GrantStartsOn = new DateTime(2026, 9, 28); vm.GrantEndsOn = new DateTime(2026, 9, 28);
        vm.PreviewCommand.Execute(null); ui.Delay(2000); ui.Pump();
        Assert.True(vm.HasPreview, vm.PreviewMessage); Assert.Equal(1, vm.Preview!.StudentCount);
        vm.CloseGrantCommand.Execute(null); ui.Pump();
    });

    [Fact]
    public void EscapeIptalOnayiniKapatirHicbirSeyDegistirmez() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("entitlements");
        var vm = ui.Entitlements;
        vm.StartsOn = new DateTime(2026, 9, 4); vm.EndsOn = new DateTime(2026, 9, 4); vm.Status = "Active"; Load(ui, vm);
        var grid = ui.FindAll<DataGrid>().Single(x => x.Name == "EntitlementsGrid");
        grid.SelectedItems.Clear(); grid.SelectedItems.Add(vm.Items[0]); ui.Pump();
        vm.RequestCancelCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsCancelConfirmationOpen);
        var id = vm.Items[0].Id.ToString().ToUpperInvariant();

        // Escape: MainWindow.PreviewKeyDown -> CloseTopmost -> CloseCancelCommand
        var source = System.Windows.PresentationSource.FromVisual(ui.Window)!;
        var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        ui.Window.RaiseEvent(args); ui.Pump();
        Assert.False(vm.IsCancelConfirmationOpen, "Escape onay penceresini kapatmadi");
        Assert.Equal(1, LiveDb.Scalar("select count(*) from meal_entitlements where upper(Id)=@p0 and Status='Active'", id));
        ui.Shot("ent-24-escape-sonrasi");
    });

    [Fact]
    public void SeciliHaklariIptalEtmeOnayVazgecVeOnayla() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("entitlements");
        var vm = ui.Entitlements;
        // Ayni adli ogrenciler: ADA'lar, 2026-09-03
        vm.StudentName = "ADA"; vm.StartsOn = new DateTime(2026, 9, 3); vm.EndsOn = new DateTime(2026, 9, 3); vm.Status = "Active"; Load(ui, vm);
        Assert.True(vm.Items.Count >= 2, "en az iki ADA satiri bekleniyordu");
        var grid = ui.FindAll<DataGrid>().Single(x => x.Name == "EntitlementsGrid");
        grid.SelectedItems.Clear(); grid.SelectedItems.Add(vm.Items[0]); grid.SelectedItems.Add(vm.Items[1]); ui.Pump();
        Assert.Equal(2, vm.SelectedItems.Count);
        ui.Shot("ent-20-secim");

        vm.RequestCancelCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsCancelConfirmationOpen, vm.ErrorMessage);
        ui.Shot("ent-21-iptal-onay");
        ui.Note("iptal onay metni: " + vm.CancelConfirmationText);

        // Vazgec: hicbir sey degismez
        var ids = vm.SelectedItems.Select(x => x.Id.ToString().ToUpperInvariant()).ToArray();
        vm.CloseCancelCommand.Execute(null); ui.Pump();
        Assert.False(vm.IsCancelConfirmationOpen);
        foreach (var id in ids) Assert.Equal(1, LiveDb.Scalar("select count(*) from meal_entitlements where upper(Id)=@p0 and Status='Active'", id));

        // Onayla: durum Iptal
        vm.RequestCancelCommand.Execute(null); ui.Pump();
        vm.ConfirmCancelCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.False(vm.IsCancelConfirmationOpen);
        Assert.False(vm.HasError, vm.ErrorMessage);
        foreach (var id in ids) Assert.Equal(1, LiveDb.Scalar("select count(*) from meal_entitlements where upper(Id)=@p0 and Status='Cancelled'", id));
        Assert.True(vm.HasStatus, "iptal sonrasi durum metni yok"); Assert.Contains("2 hak iptal edildi", vm.StatusMessage);
        // Tum durumlar: iptal satirlari listede "Iptal" ve KALAN 0; ozet kalan yalnizca aktifleri sayar
        vm.Status = null; Load(ui, vm);
        Assert.All(ids, id => Assert.Contains(vm.Items, x => x.Id.ToString().ToUpperInvariant() == id && x.Status == "Cancelled" && x.RemainingQuantity == 0));
        Assert.Equal(LiveDb.Scalar("select coalesce(sum(Quantity-ConsumedQuantity),0) from meal_entitlements e join students s on s.Id=e.StudentId where (s.FirstName || ' ' || s.LastName) like '%ADA%' and e.EntitlementDate='2026-09-03' and e.Status='Active'"), vm.RemainingQuantity);
        Assert.True(vm.TotalQuantity > vm.RemainingQuantity, "iptal edilen haklar kalan toplamindan dusmedi");
        ui.Shot("ent-22-iptal-sonrasi");

        // Iptal edilmis satiri tekrar iptal etme: Turkce uyari, modal acilmaz
        grid.SelectedItems.Clear(); grid.SelectedItems.Add(vm.Items.First(x => x.Status == "Cancelled")); ui.Pump();
        vm.RequestCancelCommand.Execute(null); ui.Pump();
        Assert.False(vm.IsCancelConfirmationOpen); Assert.True(vm.HasError);
        ui.Note("iptal edilmis satir uyarisi: " + vm.ErrorMessage);
        ui.Shot("ent-23-iptal-edilmis-uyari");
    });
}
