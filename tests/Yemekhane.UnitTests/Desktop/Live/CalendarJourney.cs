using System.Windows.Controls;
using Xunit;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Takvim / Tatil ekrani: ay gezinme, kapsam, gun rozetleri (SQLite ile birebir),
/// gun cekmecesi, tatil ve ozel istisna olusturma, toplu uygulamaya gecis.
/// </summary>
[Collection("UI")]
public class CalendarJourney
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static void WaitLoad(LiveUiHarness ui, CalendarViewModel vm)
    {
        Assert.True(LiveUiHarness.Wait(vm.LoadAsync(), Timeout)); ui.Pump();
        Assert.False(vm.HasError, vm.ErrorMessage);
    }

    [Fact]
    public void AyGezinmeKapsamVeGunRozetleriVeritabaniylaBirebir() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("holiday-transfer");
        var vm = ui.Calendar;
        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.Equal("Eylül 2026", vm.MonthTitle);
        Assert.Equal(42, vm.Days.Count);
        ui.Shot("cal-01-eylul");

        // Gun rozetleri: her gun icin ogrenci sayisi ve kullanilan/toplam SQLite ile ayni (yalnizca aktif haklar)
        foreach (var day in vm.Days.Where(x => x.IsCurrentMonth))
        {
            var date = LiveDb.Date(day.Date);
            var students = LiveDb.Scalar("select count(distinct StudentId) from meal_entitlements where EntitlementDate=@p0 and Status='Active'", date);
            var quantity = LiveDb.Scalar("select coalesce(sum(Quantity),0) from meal_entitlements where EntitlementDate=@p0 and Status='Active'", date);
            var used = LiveDb.Scalar("select coalesce(sum(ConsumedQuantity),0) from meal_entitlements where EntitlementDate=@p0 and Status='Active'", date);
            Assert.Equal(students, day.Value.Entitlements.StudentCount);
            Assert.Equal(quantity, day.Value.Entitlements.Quantity);
            Assert.Equal(used, day.Value.Entitlements.Used);
            Assert.Equal(quantity > 0, day.HasMeals);
        }
        var today = vm.Days.Single(x => x.IsToday); Assert.Equal(new DateOnly(2026, 9, 2), today.Date);
        ui.Note("bugun rozeti: " + today.MealText);
        // Hafta sonu gunlerinde hak yok
        Assert.All(vm.Days.Where(x => x.IsCurrentMonth && x.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday), x => Assert.False(x.HasMeals));

        // Ay gezinme
        vm.NextMonthCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Equal("Ekim 2026", vm.MonthTitle); Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.True(vm.IsEmpty, "Ekim'de kayit yok, bos durum metni beklenir");
        ui.Shot("cal-02-ekim-bos");
        vm.PreviousMonthCommand.Execute(null); ui.Delay(1500); ui.Pump();
        vm.PreviousMonthCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Equal("Ağustos 2026", vm.MonthTitle);
        ui.Shot("cal-03-agustos");
        vm.TodayCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Equal("Eylül 2026", vm.MonthTitle); Assert.Equal(new DateOnly(2026, 9, 2), vm.SelectedDate);

        // Kapsam: 7A sinifi -> rozetler yalnizca 7A ogrencilerini sayar
        vm.SelectedScope = vm.Scopes.Single(x => x.ScopeType == "Class" && x.Name == "7A");
        vm.ApplyScopeCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.False(vm.HasError, vm.ErrorMessage);
        var day7a = vm.Days.Single(x => x.Date == new DateOnly(2026, 9, 2));
        var db7a = LiveDb.Scalar("select count(distinct e.StudentId) from meal_entitlements e join students s on s.Id=e.StudentId join classes c on c.Id=s.ClassId where c.Name='7A' and e.EntitlementDate='2026-09-02' and e.Status='Active'");
        Assert.Equal(db7a, day7a.Value.Entitlements.StudentCount);
        ui.Note("7A kapsami 2 Eylul: " + day7a.MealText);
        ui.Shot("cal-04-kapsam-7a");
        vm.SelectedScope = vm.Scopes.First(); vm.ApplyScopeCommand.Execute(null); ui.Delay(1500); ui.Pump();

        // Gun hucrelerinde metin tasmasi yok: rozet metni hucre genisligine sigar
        var chips = ui.FindAll<TextBlock>().Where(x => x.Text.Contains("öğrenci ·")).ToList();
        Assert.True(chips.Count > 0, "rozet metni bulunamadi");
        foreach (var chip in chips)
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(chip) as System.Windows.FrameworkElement;
            Assert.True(chip.ActualWidth <= (parent?.ActualWidth ?? double.MaxValue) + 0.5, $"rozet tasti: {chip.Text}");
        }
    });

    [Fact]
    public void GunCekmecesiTatilVeIstisnaOlusturma() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("holiday-transfer");
        var vm = ui.Calendar;
        // Yolculuk tekrar kosulabilsin: daha once tatil/istisna yazilmamis, hakedisi olan bir is gunu sec.
        var used = LiveDb.Rows("select Date from holidays union select Date from schedule_exceptions").Select(r => r[0]!).ToHashSet(StringComparer.Ordinal);
        var date = Enumerable.Range(0, 12).Select(i => new DateOnly(2026, 9, 14).AddDays(i))
            .First(x => x.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !used.Contains(LiveDb.Date(x))
                && LiveDb.Scalar("select count(*) from meal_entitlements where EntitlementDate=@p0 and Status='Active'", LiveDb.Date(x)) > 0);
        ui.Note("secilen gun: " + date);
        Assert.True(LiveUiHarness.Wait(vm.SelectDayAsync(date), Timeout)); ui.Pump();
        Assert.True(vm.IsDrawerOpen); Assert.NotNull(vm.SelectedDetails);
        var details = vm.SelectedDetails!;
        var d = LiveDb.Date(date);
        Assert.Equal(LiveDb.Scalar("select coalesce(sum(Quantity),0) from meal_entitlements where EntitlementDate=@p0 and Status='Active'", d), details.Entitlements.Quantity);
        Assert.Equal(LiveDb.Scalar("select coalesce(sum(ConsumedQuantity),0) from meal_entitlements where EntitlementDate=@p0 and Status='Active'", d), details.Entitlements.Used);
        var meal = Assert.Single(details.Meals); Assert.Equal("Öğle Yemeği", meal.MealName);
        Assert.Equal(details.Entitlements.Quantity, meal.Quantity);
        Assert.Equal(date.ToDateTime(TimeOnly.MinValue).ToString("d MMMM yyyy, dddd", System.Globalization.CultureInfo.GetCultureInfo("tr-TR")), vm.SelectedDateTitle);
        Assert.Contains("Eylül 2026", vm.SelectedDateTitle);
        Assert.True(vm.HasNoOperations);
        ui.Shot("cal-10-gun-cekmecesi");

        // Tatil: bos ad -> Turkce hata, kayit yok
        vm.OpenHolidayFormCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsHolidayFormOpen);
        ui.Shot("cal-11-tatil-formu");
        vm.HolidayName = ""; vm.CreateHolidayCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(vm.IsHolidayFormOpen); Assert.False(string.IsNullOrWhiteSpace(vm.FormMessage), "bos ad icin mesaj yok");
        Assert.Contains("Tatil adı", vm.FormMessage);
        Assert.Equal(0, LiveDb.Scalar("select count(*) from holidays where Date=@p0", d));
        ui.Note("bos tatil adi mesaji: " + vm.FormMessage);
        ui.Shot("cal-12-tatil-bos-ad");
        // Tek harf: sunucu kurali (2-200) Turkce olarak ulasir
        vm.HolidayName = "X"; vm.CreateHolidayCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(vm.IsHolidayFormOpen); Assert.Contains("2-200", vm.FormMessage);

        // Gecerli tatil: NextBusinessDay davranisi
        vm.HolidayName = "Deneme Tatili"; vm.HolidayType = "Administrative"; vm.TransferBehavior = "NextBusinessDay";
        vm.CreateHolidayCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.False(vm.IsHolidayFormOpen, vm.FormMessage);
        Assert.Equal(1, LiveDb.Scalar("select count(*) from holidays where Date=@p0 and Name='Deneme Tatili' and HolidayType='Administrative' and TransferBehavior='NextBusinessDay'", d));
        Assert.Equal(1, LiveDb.Scalar("select count(*) from holiday_scopes hs join holidays h on h.Id=hs.HolidayId where h.Date=@p0 and hs.ScopeType='AllSchool'", d));
        // Gun hucresi tatil rozeti tasir
        var cell = vm.Days.Single(x => x.Date == date);
        Assert.True(cell.HasHoliday); Assert.Equal("Deneme Tatili", cell.HolidayText);
        // Cekmece: olay listesi Turkce, bilgi metni sonraki adimi soyler
        Assert.Contains(vm.SelectedOperations, x => x.Title.Contains("Deneme Tatili") && x.Detail!.Contains("Sonraki iş gününe aktar"));
        Assert.True(vm.HasInfo, "tatil sonrasi bilgi metni yok");
        ui.Note("tatil bilgi metni: " + vm.InfoMessage);
        Assert.Contains("toplu uygula", vm.InfoMessage!, StringComparison.OrdinalIgnoreCase);
        // Tatil kaydi haklari KENDISI degistirmez: aktif hak sayisi ayni
        Assert.Equal(details.Entitlements.Quantity, vm.SelectedDetails!.Entitlements.Quantity);
        Assert.Equal(0, LiveDb.Scalar("select count(*) from meal_transfers where OriginalDate=@p0", d));
        ui.Shot("cal-13-tatil-olusturuldu");

        // Ekranda ham kod yok
        var texts = ui.FindAll<TextBlock>().Where(x => x.IsVisible).Select(x => x.Text).ToList();
        Assert.DoesNotContain("NextBusinessDay", texts); Assert.DoesNotContain("Administrative", texts); Assert.DoesNotContain("Delete", texts);

        // Ozel istisna
        vm.OpenExceptionFormCommand.Execute(null); ui.Pump();
        Assert.True(vm.IsExceptionFormOpen);
        vm.ExceptionType = "Trip"; vm.ExceptionBehavior = "Keep"; vm.ExceptionDescription = "Müze gezisi";
        ui.Shot("cal-14-istisna-formu");
        vm.CreateExceptionCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.False(vm.IsExceptionFormOpen, vm.FormMessage);
        Assert.Equal(1, LiveDb.Scalar("select count(*) from schedule_exceptions where Date=@p0 and ExceptionType='Trip' and EntitlementBehavior='Keep' and Description='Müze gezisi'", d));
        Assert.True(vm.Days.Single(x => x.Date == date).HasTrip);
        Assert.Contains(vm.SelectedOperations, x => x.Title.Contains("Gezi") && x.Detail == "Müze gezisi");
        ui.Shot("cal-15-istisna-olusturuldu");

        // Toplu uygula: sihirbaz secili tarih ve tatilin davranisiyla acilir
        var wizard = ui.CalendarBulk;
        Assert.True(LiveUiHarness.Wait(wizard.InitializeAsync(), Timeout)); // harness eksigi (bkz. ustteki not)
        vm.OpenBulkCommand.Execute(null); ui.Pump();
        Assert.Equal("NextBusinessDay", wizard.TransferBehavior);
        Assert.True(wizard.IsOpen);
        Assert.Equal(date.ToDateTime(TimeOnly.MinValue), wizard.StartsOn); Assert.Equal(date.ToDateTime(TimeOnly.MinValue), wizard.EndsOn);
        ui.Shot("cal-16-toplu-sihirbaz");
        wizard.CloseCommand.Execute(null); ui.Pump();

        // Escape cekmeceyi kapatir
        var source = System.Windows.PresentationSource.FromVisual(ui.Window)!;
        var args = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.Escape)
        { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
        ui.Window.RaiseEvent(args); ui.Pump();
        Assert.False(vm.IsDrawerOpen, "Escape cekmeceyi kapatmadi");
    });

    [Fact]
    public void TatilSonrasiTopluIptalGunRozetiniGunceller() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll(); ui.Navigate("holiday-transfer");
        var vm = ui.Calendar; var wizard = ui.CalendarBulk;
        // LoadAll yalnizca Hakedisler'in sihirbazini yukler; uygulama (App.xaml.cs:210) takvimin
        // sihirbazini da yukler. Harness eksigi, gercek hata degil.
        Assert.True(LiveUiHarness.Wait(wizard.InitializeAsync(), Timeout));
        var date = new DateOnly(2026, 9, 17); var d = LiveDb.Date(date);
        Assert.True(LiveUiHarness.Wait(vm.SelectDayAsync(date), Timeout)); ui.Pump();
        var before = vm.SelectedDetails!.Entitlements.Quantity;
        Assert.True(before > 0);

        vm.OpenHolidayFormCommand.Execute(null); ui.Pump();
        vm.HolidayName = "İptal Tatili"; vm.TransferBehavior = "Delete";
        vm.CreateHolidayCommand.Execute(null); ui.Delay(3000); ui.Pump();
        Assert.False(vm.IsHolidayFormOpen, vm.FormMessage);

        // O gun hala aktif hakki olan ilk sinif kapsaminda toplu iptal (yolculuk tekrar kosulabilsin)
        var className = LiveDb.Rows("select c.Name from meal_entitlements e join students s on s.Id=e.StudentId join classes c on c.Id=s.ClassId where e.EntitlementDate=@p0 and e.Status='Active' and e.Quantity>e.ConsumedQuantity group by c.Name order by c.Name", d).Select(r => r[0]!).First();
        ui.Note("toplu iptal sinifi: " + className);
        vm.OpenBulkCommand.Execute(null); ui.Pump();
        Assert.True(wizard.IsOpen); Assert.Equal("Delete", wizard.TransferBehavior);
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        wizard.SelectedScope = wizard.Scopes.Single(x => x.ScopeType == "Class" && x.Name == className);
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        Assert.Equal(date.ToDateTime(TimeOnly.MinValue), wizard.StartsOn);
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        wizard.NextCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.Equal(5, wizard.Step);
        var expected = LiveDb.Scalar("select count(*) from meal_entitlements e join students s on s.Id=e.StudentId join classes c on c.Id=s.ClassId where c.Name=@p1 and e.EntitlementDate=@p0 and e.Status='Active' and e.Quantity>e.ConsumedQuantity", d, className);
        Assert.True(expected > 0);
        Assert.Equal(expected, wizard.Preview!.EntitlementCount);
        ui.Shot("cal-20-toplu-onizleme-sinif");
        wizard.NextCommand.Execute(null); ui.Delay(300); ui.Pump();
        wizard.ApplyCommand.Execute(null); ui.Delay(3500); ui.Pump();
        Assert.True(wizard.Step == 7, $"uygulama basarisiz (adim {wizard.Step}): {wizard.ErrorMessage}");
        Assert.Equal(0, LiveDb.Scalar("select count(*) from meal_entitlements e join students s on s.Id=e.StudentId join classes c on c.Id=s.ClassId where c.Name=@p1 and e.EntitlementDate=@p0 and e.Status='Active'", d, className));
        wizard.CloseCommand.Execute(null); ui.Delay(2000); ui.Pump();

        // Takvim kendini yeniledi: gun rozeti ve cekmece yalnizca AKTIF haklari sayar
        var cell = vm.Days.Single(x => x.Date == date);
        Assert.Equal(before - expected, cell.Value.Entitlements.Quantity);
        Assert.Equal(before - expected, vm.SelectedDetails!.Entitlements.Quantity);
        ui.Note($"17 Eylul: once {before}, toplu iptal {expected}, sonra {cell.MealText}");
        ui.Shot("cal-21-toplu-iptal-sonrasi");
    });
}
