using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Sms;

/// <summary>
/// Veliye giden SMS'teki <c>{{Amount}}</c> degiskeni NOKTA-ONDALIK yazimda 100 kat
/// sismemelidir.
///
/// <para>
/// <c>decimal.TryParse(Amount, NumberStyles.Number, CultureInfo.CurrentCulture, ...)</c>
/// kullaniliyordu ve <c>App.xaml.cs</c> <c>CurrentCulture</c>'i tr-TR'ye sabitliyor.
/// tr-TR'de grup ayiraci NOKTA oldugu icin "250.50" -> 25050 okunuyor ve veliye
/// "25.050,00 TL borcunuz var" yazan bir mesaj gidiyordu.
/// </para>
/// <para>
/// Ayni hata Kasa'da (CashViewModelRegressionTests) ve Ucret Plani'nda bulunmustu;
/// cozum <see cref="CashViewModel.TryParseAmount"/> ile tek yerde toplandi. Burasi
/// hatanin ucuncu ve en gorunur yeriydi: yanlis rakam KULLANICIYA DEGIL, VELIYE gidiyor
/// ve okul geri alamiyor.
/// </para>
/// </summary>
public sealed class SmsAmountParsingTests
{
    [Theory]
    [InlineData("250.50", 250.50)]      // Ingilizce klavye yazimi -- ONCE 25.050 oluyordu
    [InlineData("1250.50", 1_250.50)]
    [InlineData("6000.00", 6_000)]
    [InlineData("250,50", 250.50)]      // tr-TR dogru yazim korunmali
    [InlineData("1.250,50", 1_250.50)]
    [InlineData("500", 500)]
    public async Task TutarDegiskeniNoktaOndalikYazimdaSismez(string text, decimal expected)
    {
        var api = new CapturingSmsApi();
        using var vm = new SmsViewModel(api, ["sms.read", "sms.send", "sms.manage"]);
        await vm.InitializeAsync();
        vm.Students[0].IsSelected = true;
        vm.CustomMessage = "Merhaba";
        vm.Amount = text;

        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);

        Assert.NotNull(api.LastPreview);
        Assert.Equal(expected, Assert.IsType<decimal>(api.LastPreview!.Variables!["Amount"]));
    }

    private sealed class CapturingSmsApi : ISmsApiClient
    {
        private readonly Guid studentId = Guid.NewGuid();
        public BulkSmsRequest? LastPreview { get; private set; }

        public Task<BulkSmsPreview> PreviewAsync(BulkSmsRequest request, CancellationToken cancellationToken = default)
        {
            LastPreview = request;
            return Task.FromResult(new BulkSmsPreview(1, 1, 0, 0,
                [new(studentId, "Ada Yılmaz", "Veli", "+905321112233", "Merhaba")], "token",
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<SmsTargetOptions> TargetsAsync(string? search, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SmsTargetOptions([new(studentId, "1", "Ada Yılmaz")], [], []));
        public Task<BulkSmsEnqueueResult> ApplyAsync(ApplyBulkSmsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BulkSmsEnqueueResult(1, 0, false));
        public Task<IReadOnlyList<SmsTemplateDetails>> TemplatesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SmsTemplateDetails>>([new(Guid.NewGuid(), "Bilgi", "Merhaba {{StudentName}}", true)]);
        public Task<SmsTemplateDetails> SaveTemplateAsync(Guid? id, SaveSmsTemplateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SmsTemplateDetails(id ?? Guid.NewGuid(), request.Name, request.Body, request.IsActive));
        public Task DeactivateTemplateAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<SmsLogDetails>> HistoryAsync(SmsHistoryFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<SmsLogDetails>([], 1, 50, 0));
        public Task RetryAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
