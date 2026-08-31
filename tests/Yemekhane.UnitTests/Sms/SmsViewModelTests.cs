using System.Net;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Sms;

public sealed class SmsViewModelTests
{
    [Fact]
    public async Task SendRequiresPreviewAndConfirmationAndLoadsAllComponents()
    {
        var api = new FakeSmsApi();
        using var vm = new SmsViewModel(api, ["sms.read", "sms.send", "sms.manage"]);
        await vm.InitializeAsync();
        vm.Students[0].IsSelected = true; vm.CustomMessage = "Merhaba";

        vm.PreviewCommand.Execute(null);
        await UntilAsync(() => vm.HasPreview);
        Assert.False(vm.EnqueueCommand.CanExecute(null));
        vm.IsConfirmed = true;
        vm.EnqueueCommand.Execute(null);
        await UntilAsync(() => api.ApplyCount == 1);

        Assert.Equal(1, api.ApplyCount);
        Assert.NotEmpty(vm.Templates);
        Assert.NotEmpty(vm.History);
        Assert.Equal(2, SmsViewModel.SmsSegments(new string('a', 161)));
    }

    [Fact]
    public void StudentDetailSmsActionNavigatesToSelectedStudent()
    {
        var id = Guid.NewGuid();
        var navigation = new ShellNavigationService([ShellRoutes.Students, ShellRoutes.StudentDetail, ShellRoutes.Sms]);
        string? route = null; navigation.NavigationRequested += (_, e) => route = e.Route;
        using var vm = new StudentsViewModel(new StudentApiStub(), navigation, ["students.read", "sms.send"]);
        vm.SelectedStudent = new(id, "1", null, "A", "B", null, null, null, null, true, 0, false, null);

        vm.OpenSmsCommand.Execute(null);

        Assert.Equal($"sms/{id:D}", route);
    }

    [Fact]
    public void SmsXamlContainsTabsConfirmationAndRealBindings()
    {
        var path = Path.Combine(FindRoot(), "src", "Yemekhane.Desktop", "Views", "SmsView.xaml");
        var xaml = File.ReadAllText(path);
        Assert.Contains("Header=\"Gönder\"", xaml);
        Assert.Contains("Header=\"Şablonlar\"", xaml);
        Assert.Contains("Header=\"Geçmiş\"", xaml);
        Assert.Contains("IsConfirmed", xaml);
        Assert.DoesNotContain("fake", xaml, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task UntilAsync(Func<bool> predicate)
    {
        for (var i = 0; i < 100 && !predicate(); i++) await Task.Delay(10);
        Assert.True(predicate());
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private sealed class FakeSmsApi : ISmsApiClient
    {
        private readonly Guid studentId = Guid.NewGuid();
        public int ApplyCount { get; private set; }
        public Task<SmsTargetOptions> TargetsAsync(string? search, CancellationToken cancellationToken = default) => Task.FromResult(new SmsTargetOptions([new(studentId, "1", "Ada Yılmaz")], [], []));
        public Task<BulkSmsPreview> PreviewAsync(BulkSmsRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new BulkSmsPreview(1, 1, 0, 0, [new(studentId, "Ada Yılmaz", "Veli", "+905321112233", "Merhaba")], "token", DateTimeOffset.UtcNow.AddMinutes(5)));
        public Task<BulkSmsEnqueueResult> ApplyAsync(ApplyBulkSmsRequest request, CancellationToken cancellationToken = default) { ApplyCount++; return Task.FromResult(new BulkSmsEnqueueResult(1, 0, false)); }
        public Task<IReadOnlyList<SmsTemplateDetails>> TemplatesAsync(bool includeInactive, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SmsTemplateDetails>>([new(Guid.NewGuid(), "Bilgi", "Merhaba {{StudentName}}", true)]);
        public Task<SmsTemplateDetails> SaveTemplateAsync(Guid? id, SaveSmsTemplateRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new SmsTemplateDetails(id ?? Guid.NewGuid(), request.Name, request.Body, request.IsActive));
        public Task DeactivateTemplateAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<PagedResult<SmsLogDetails>> HistoryAsync(SmsHistoryFilter filter, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResult<SmsLogDetails>([new(Guid.NewGuid(), studentId, null, "+905321112233", "Merhaba", "Mock", SmsLogStatuses.Sent, "key", 1, null, null, DateTimeOffset.UtcNow, "p", null, DateTimeOffset.UtcNow)], 1, 50, 1));
        public Task RetryAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StudentApiStub : IStudentApiClient
    {
        public Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken = default) => Task.FromResult(new PagedResult<StudentListItem>([], 1, 50, 0));
        public Task<StudentDetails> GetAsync(Guid value, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<StudentDetails> SaveAsync(Guid? value, SaveStudentRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeactivateAsync(Guid value, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<object>> LoadTabAsync(string tab, Guid studentId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<object>>([]);
        public Task GiveLeaveAsync(Yemekhane.Application.Leaves.CreateLeaveRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReplaceCardAsync(Guid studentId, Yemekhane.Application.Cards.ReplaceCardRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
