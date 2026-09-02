using System.Net;
using Yemekhane.Application.StudentImports;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.StudentImports;

/// <summary>
/// Sunucunun reddi (ProblemDetails basligi) Sicil Aktar ekraninda AYNEN gorunmeli;
/// once yalnizca "Dosya okunamadı" geliyor, hangi basligin eksik oldugu soylenmiyordu.
/// </summary>
public sealed class StudentImportViewModelErrorTests
{
    [Fact]
    public async Task SunucununTurkceRedMesajiEkranaUlasir()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "NUMARA;ISIM\r\n1;Ali\r\n");
            var api = new RejectingApi("Zorunlu başlıklar eksik: NO, KART NO, AD, SOYAD.");
            var vm = new StudentImportViewModel(api, new StubDialogs { OpenResult = path }, ["students.write"]);

            vm.ChooseFileCommand.Execute(null);
            await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);

            Assert.Equal("Zorunlu başlıklar eksik: NO, KART NO, AD, SOYAD.", vm.ErrorMessage);
            Assert.False(vm.HasPreview);
        }
        finally { File.Delete(path); }
    }

    private sealed class StubDialogs : IFileDialogService
    {
        public string? OpenResult { get; set; }
        public string? OpenFile(string title, string filter) => OpenResult;
        public string? SaveFile(string title, string filter, string suggestedFileName) => null;
    }

    private sealed class RejectingApi(string message) : IStudentImportApiClient
    {
        public Task<ImportPreviewResult> PreviewAsync(string filePath, CancellationToken cancellationToken = default) =>
            throw new ApiRequestException(message, HttpStatusCode.BadRequest);
        public Task<ImportApplyResult> ApplyAsync(ApplyStudentImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DownloadErrorReportAsync(string token, string targetPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
