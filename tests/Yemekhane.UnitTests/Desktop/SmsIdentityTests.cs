using System.Windows;
using System.Windows.Controls;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Duzeltme turu son, Kritik 2: SMS gonderim onizlemesi operatorun dispatch'ten
/// ONCE okudugu son ekrandir. Onizleme ogesi (SmsRecipientPreview) yalnizca
/// StudentName, Phone ve Message gosteriyordu -- veritabaninda ayni isimden
/// birden fazla ogrenci varken (Ada Katirci, Ada Haslamaci, Ada Soylemez gibi)
/// operator uc onizleme satirinin ucunu de "ADA ..." olarak gorur ve hangisinin
/// hangisi oldugunu AYIRT EDEMEZ -- yanlis veliye borc hatirlatmasi gonderilir.
///
/// SmsRecipientPreview DONDURULMUS Yemekhane.Application projesindedir ve
/// StudentNo/sinif TASIMAZ -- yalnizca StudentId, StudentName, ParentName,
/// Phone, Message alanlari vardir. Ancak ParentName zaten kayittadir ve
/// ekranda hic KULLANILMIYORDU. Ayni isimli uc ogrencinin velisi FARKLI
/// oldugundan, ad+veli+telefon UCLUSU gercekten ayirt edicidir. Duzeltme
/// SmsView.xaml'deki onizleme sablonuna "Veli: {ParentName}" satirini ekler.
///
/// Bu testler XAML METNINI degil, KURULMUS gorsel agaci olcer.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class SmsIdentityTests
{
    /// <summary>
    /// Asil guvenlik dogrulamasi: onizleme kartinda veli adi GERCEKTEN
    /// gorseldir ve ogrenci adiyla AYNI kartta gorunur -- ayri bir yerde
    /// veya yanlislikla baska bir ogrencinin kartinda degil.
    /// </summary>
    [Fact]
    public void OnizlemeKartiVeliAdiniGosterir() =>
        UiThread.Run(() =>
        {
            var api = new FakeSmsApi();
            var vm = new SmsViewModel(api, ["sms.read", "sms.send", "sms.manage"]);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.Students[0].IsSelected = true;
            vm.CustomMessage = "Merhaba";
            vm.PreviewCommand.Execute(null);
            UntilAsync(() => vm.HasPreview).GetAwaiter().GetResult();

            var view = new SmsView { DataContext = vm };
            Host(view);

            var texts = Descendants(view).OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            Assert.Contains(texts, t => t!.Contains("Ada Katırcı", StringComparison.Ordinal));
            Assert.Contains(texts, t => t!.Contains("Veli", StringComparison.Ordinal));
            Assert.Contains(texts, t => t!.Contains("Ada Katırcı'nın Velisi", StringComparison.Ordinal));
        });

    /// <summary>
    /// Ayirt edicilik kaniti: iki AYNI ISIMLI ogrencinin onizleme kartlari
    /// FARKLI veli adlariyla gorunmeli -- yalnizca isimle karistirilmasin.
    /// </summary>
    [Fact]
    public void AyniIsimliOgrencilerinOnizlemeKartlariVeliAdiylaAyrisir() =>
        UiThread.Run(() =>
        {
            var api = new FakeSmsApi { UseAmbiguousNames = true };
            var vm = new SmsViewModel(api, ["sms.read", "sms.send", "sms.manage"]);
            vm.InitializeAsync().GetAwaiter().GetResult();
            vm.Students[0].IsSelected = true;
            vm.CustomMessage = "Merhaba";
            vm.PreviewCommand.Execute(null);
            UntilAsync(() => vm.HasPreview).GetAwaiter().GetResult();

            var view = new SmsView { DataContext = vm };
            Host(view);

            var texts = Descendants(view).OfType<TextBlock>()
                .Select(t => t.Text)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();

            // Iki "Ada Katırcı" karti var; velileri farkli olmali ve her ikisi
            // de gorsel agacta gorunmeli.
            Assert.Contains(texts, t => t!.Contains("Katırcı Velisi", StringComparison.Ordinal));
            Assert.Contains(texts, t => t!.Contains("Haşlamacı Velisi", StringComparison.Ordinal));
        });

    private static Border Host(FrameworkElement view)
    {
        var host = UiThread.Host(view, 1440, 900);
        host.Measure(new Size(1440, 900));
        host.Arrange(new Rect(0, 0, 1440, 900));
        host.UpdateLayout();
        return host;
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed class FakeSmsApi : ISmsApiClient
    {
        private readonly Guid studentId = Guid.NewGuid();
        private readonly Guid studentId2 = Guid.NewGuid();
        public bool UseAmbiguousNames { get; init; }

        public Task<SmsTargetOptions> TargetsAsync(string? search, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SmsTargetOptions([new(studentId, "1", "Ada Katırcı")], [], []));

        public Task<BulkSmsPreview> PreviewAsync(BulkSmsRequest request, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SmsRecipientPreview> examples = UseAmbiguousNames
                ? [
                    new(studentId, "Ada Katırcı", "Ada Katırcı'nın Katırcı Velisi", "+905321112233", "Merhaba"),
                    new(studentId2, "Ada Katırcı", "Ada Katırcı'nın Haşlamacı Velisi", "+905321112244", "Merhaba")
                  ]
                : [new(studentId, "Ada Katırcı", "Ada Katırcı'nın Velisi", "+905321112233", "Merhaba")];

            return Task.FromResult(new BulkSmsPreview(examples.Count, examples.Count, 0, 0, examples, "token", DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<BulkSmsEnqueueResult> ApplyAsync(ApplyBulkSmsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BulkSmsEnqueueResult(1, 0, false));

        public Task<IReadOnlyList<SmsTemplateDetails>> TemplatesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SmsTemplateDetails>>([]);

        public Task<SmsTemplateDetails> SaveTemplateAsync(Guid? id, SaveSmsTemplateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SmsTemplateDetails(id ?? Guid.NewGuid(), request.Name, request.Body, request.IsActive));

        public Task DeactivateTemplateAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PagedResult<SmsLogDetails>> HistoryAsync(SmsHistoryFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<SmsLogDetails>([], 1, 50, 0));

        public Task RetryAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
