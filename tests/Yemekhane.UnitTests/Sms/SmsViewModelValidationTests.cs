using System.Net;
using System.Net.Http;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Sms;

/// <summary>
/// SMS ekraninin canli denetimde bulunan bosluklari: sunucu mesaji kullaniciya ulasmiyor
/// ("Çevrimdışı" sayiliyordu), secim aramada kayboluyordu, pasif sablon Gonder kutusuna
/// dusuyordu, degisken adlari İngilizce kaliyordu.
/// </summary>
public sealed class SmsViewModelValidationTests
{
    [Fact]
    public async Task YerelDogrulamaSunucuyaGitmedenTurkceMesajVerir()
    {
        var api = new FakeSmsApi();
        using var vm = new SmsViewModel(api, ["sms.read", "sms.send", "sms.manage"]);
        await vm.InitializeAsync();

        vm.CustomMessage = "Merhaba";
        Assert.Equal("En az bir öğrenci seçin: listedeki 'Seç' kutusunu işaretleyin.", vm.ValidateSend());
        vm.Students[0].IsSelected = true;
        Assert.Null(vm.ValidateSend());
        vm.CustomMessage = "   ";
        Assert.Equal("Mesaj metni boş olamaz.", vm.ValidateSend());
        vm.TargetType = "Class"; vm.CustomMessage = "Merhaba";
        Assert.Equal("Sınıf hedefi için bir sınıf seçin.", vm.ValidateSend());
        vm.TargetType = "Group";
        Assert.Equal("Grup hedefi için bir grup seçin.", vm.ValidateSend());
        vm.TargetType = "All"; vm.UseTemplate = true;
        Assert.StartsWith("Bir şablon seçin", vm.ValidateSend(), StringComparison.Ordinal);
        vm.SelectedTemplate = vm.SendTemplates.Single(t => t.Body.Contains("{{ExpiryDate}}", StringComparison.Ordinal));
        Assert.Contains("'Son tarih'", vm.ValidateSend(), StringComparison.Ordinal);
        vm.ExpiryDate = "15.09.2026";
        Assert.Null(vm.ValidateSend());

        vm.ExpiryDate = "";
        vm.PreviewCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(0, api.PreviewCount);
        Assert.False(vm.HasPreview);
        Assert.Contains("'Son tarih'", vm.SendError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SecimAramalarArasindaKorunurVeIstegeGirer()
    {
        var api = new FakeSmsApi();
        using var vm = new SmsViewModel(api, ["sms.read", "sms.send", "sms.manage"]);
        await vm.InitializeAsync();
        var first = vm.Students[0].Id;
        vm.Students[0].IsSelected = true;

        vm.Search = "baska";
        await ((AsyncCommand)vm.SearchStudentsCommand).ExecuteAsync(null);
        Assert.DoesNotContain(vm.Students, s => s.Id == first);
        Assert.Equal(1, vm.SelectedStudentCount);
        vm.Students[0].IsSelected = true;
        Assert.Equal(2, vm.SelectedStudentCount);
        Assert.Equal("Seçili: 2 öğrenci", vm.SelectedStudentText);

        vm.CustomMessage = "Merhaba";
        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);
        Assert.NotNull(api.LastPreviewRequest);
        Assert.Equal(2, api.LastPreviewRequest!.Scope.StudentIds!.Count);
        Assert.Contains(first, api.LastPreviewRequest.Scope.StudentIds!);

        vm.ClearSelectionCommand.Execute(null);
        Assert.Equal(0, vm.SelectedStudentCount);
        Assert.All(vm.Students, s => Assert.False(s.IsSelected));
    }

    [Fact]
    public async Task GonderKutusuYalnizAktifSablonlariListeler()
    {
        var api = new FakeSmsApi();
        using var vm = new SmsViewModel(api, ["sms.read", "sms.send", "sms.manage"]);
        await vm.InitializeAsync();
        Assert.DoesNotContain(vm.Templates, t => !t.IsActive);

        vm.IncludeInactive = true;
        await Task.Delay(50);
        Assert.Contains(vm.Templates, t => !t.IsActive);
        Assert.DoesNotContain(vm.SendTemplates, t => !t.IsActive);
        Assert.True(api.IncludeInactiveRequested);
    }

    [Fact]
    public async Task SunucununReddiMesajiylaGosterilirVeCevrimdisiSayilmaz()
    {
        var api = new FakeSmsApi { PreviewFailure = new ApiRequestException("'ExpiryDate' şablon değişkeni için değer verilmelidir.", HttpStatusCode.BadRequest) };
        using var vm = new SmsViewModel(api, ["sms.read", "sms.send", "sms.manage"]);
        await vm.InitializeAsync();
        vm.Students[0].IsSelected = true; vm.CustomMessage = "Merhaba";

        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);

        Assert.Equal("'Son tarih' şablon değişkeni için değer verilmelidir.", vm.SendError);
        Assert.False(vm.IsOffline);
        Assert.False(vm.HasPreview);

        api.PreviewFailure = new HttpRequestException("baglanti yok");
        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);
        Assert.Equal("SMS servisine ulaşılamadı.", vm.SendError);
        Assert.True(vm.IsOffline);
    }

    [Fact]
    public void DegiskenAdlariTurkcelestirilir()
    {
        Assert.Equal("'Tutar' şablon değişkeni boş olamaz.", SmsViewModel.Localize("'Amount' şablon değişkeni boş olamaz."));
        Assert.Equal("Bilinmeyen metin", SmsViewModel.Localize("Bilinmeyen metin"));
    }

    [Fact]
    public void SeceneklerTurkceAdVeIngilizceDegerTasir()
    {
        using var vm = new SmsViewModel(new FakeSmsApi(), ["sms.read", "sms.send", "sms.manage"]);
        Assert.Equal(["Manual", "Class", "Group", "All", "Filter"], vm.TargetTypes.Select(x => x.Value).ToArray());
        Assert.All(vm.TargetTypes, x => Assert.NotEqual(x.Value, x.Name));
        Assert.Equal("", vm.HistoryStatuses[0].Value);
        Assert.Equal("Tümü", vm.HistoryStatuses[0].Name);
        Assert.Contains(vm.HistoryStatuses, x => x.Value == SmsLogStatuses.RetryScheduled && x.Name == "Yeniden denenecek");
    }

    [Fact]
    public void TurkceKarakterSegmentiYetmiseDusurur()
    {
        var ascii = new string('a', 100);
        var turkish = new string('ğ', 71);
        Assert.Equal(1, SmsViewModel.SmsSegments(ascii));
        Assert.Equal(2, SmsViewModel.SmsSegments(turkish));
        Assert.True(SmsViewModel.UsesUnicode("ışık"));
        Assert.False(SmsViewModel.UsesUnicode("isik"));
    }

    private sealed class FakeSmsApi : ISmsApiClient
    {
        private readonly Guid ada = Guid.NewGuid(), ali = Guid.NewGuid();
        private readonly Guid activeTemplate = Guid.NewGuid(), dateTemplate = Guid.NewGuid(), inactiveTemplate = Guid.NewGuid();
        public int PreviewCount { get; private set; }
        public BulkSmsRequest? LastPreviewRequest { get; private set; }
        public Exception? PreviewFailure { get; set; }
        public bool IncludeInactiveRequested { get; private set; }

        public Task<SmsTargetOptions> TargetsAsync(string? search, CancellationToken cancellationToken = default) =>
            Task.FromResult(search is null
                ? new SmsTargetOptions([new(ada, "5001", "Ada Akgün", "7", "B")], [new(Guid.NewGuid(), "7B")], [])
                : new SmsTargetOptions([new(ali, "5002", "Ali Arslan", "8", "C")], [], []));

        public Task<BulkSmsPreview> PreviewAsync(BulkSmsRequest request, CancellationToken cancellationToken = default)
        {
            PreviewCount++; LastPreviewRequest = request;
            if (PreviewFailure is not null) throw PreviewFailure;
            return Task.FromResult(new BulkSmsPreview(1, 1, 0, 0, [new(ada, "Ada Akgün", "Veli", "+905321112233", "Merhaba")], "token", DateTimeOffset.UtcNow.AddMinutes(5)));
        }

        public Task<BulkSmsEnqueueResult> ApplyAsync(ApplyBulkSmsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new BulkSmsEnqueueResult(1, 0, false));

        public Task<IReadOnlyList<SmsTemplateDetails>> TemplatesAsync(bool includeInactive, CancellationToken cancellationToken = default)
        {
            IncludeInactiveRequested |= includeInactive;
            var list = new List<SmsTemplateDetails>
            {
                new(activeTemplate, "Bilgi", "Merhaba {{StudentName}}", true),
                new(dateTemplate, "Tarihli", "Son tarih {{ExpiryDate}}", true)
            };
            if (includeInactive) list.Add(new(inactiveTemplate, "Eski", "Pasif", false));
            return Task.FromResult<IReadOnlyList<SmsTemplateDetails>>(list);
        }

        public Task<SmsTemplateDetails> SaveTemplateAsync(Guid? id, SaveSmsTemplateRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SmsTemplateDetails(id ?? Guid.NewGuid(), request.Name, request.Body, request.IsActive));
        public Task DeactivateTemplateAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<SmsLogDetails>> HistoryAsync(SmsHistoryFilter filter, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<SmsLogDetails>([], 1, 50, 0));
        public Task RetryAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
