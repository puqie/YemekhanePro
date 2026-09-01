using System.IO;
using System.Net.Http;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.UnitTests.Api;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Sicil Aktar ekrani: dosya secilir, onizlenir, uygulanir -- ve VERITABANI kontrol edilir.
///
/// Ara katmanlarin hicbiri taklit edilmez: ViewModel -> HTTP istemcisi -> denetleyici ->
/// icе aktarma servisi -> veritabani zincirinin tamami gercektir. Yalnizca dosya secme
/// diyalogu degistirilir; cunku bassiz bir kosuda Windows diyalogu acilamaz.
/// </summary>
[Collection(UiCollection.Name)]
public sealed class StudentImportJourneyTests : IAsyncLifetime
{
    private readonly YemekhaneApiFactory factory = new();
    private readonly List<string> temporaryFiles = [];
    private HttpClient client = null!;

    public Task InitializeAsync()
    {
        client = factory.CreateOperatorClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var path in temporaryFiles) if (File.Exists(path)) File.Delete(path);
        client.Dispose();
        await factory.DisposeAsync();
    }

    private StudentImportViewModel NewScreen(StubFileDialogs dialogs) => new(
        new StudentImportApiClient(client, new OperatorSession()), dialogs,
        ["students.read", "students.write"]);

    private Task<T> InScope<T>(Func<YemekhaneDbContext, Task<T>> query)
    {
        var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        return query(db).ContinueWith(t => { scope.DisposeAsync().AsTask().Wait(); return t.Result; });
    }

    /// <summary>Gercek bir CSV dosyasi olusturur; icerik sunucu tarafindan ayristirilir.</summary>
    private string WriteCsv(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"sicil-{Guid.NewGuid():N}.csv");
        // UTF-8 BOM: Turkce karakterlerin dogru okunmasi bu testin konusu.
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        temporaryFiles.Add(path);
        return path;
    }

    [Fact]
    public async Task UserPicksAFilePreviewsItAndTheStudentsLandInTheDatabase()
    {
        var path = WriteCsv("NO;KART NO;AD;SOYAD\nIMP-001;KART-001;Çağrı;Şahinoğlu\nIMP-002;KART-002;Öznur;Güngör\n");
        var screen = NewScreen(new StubFileDialogs { OpenResult = path });

        // Kullanici "Dosya Seç" butonuna basar.
        screen.ChooseFileCommand.Execute(null);
        Assert.True(screen.HasFile, "Dosya seçildi ama ekran dosyayı almadı.");

        // Kullanici "Önizle" butonuna basar.
        await Execute(screen.PreviewCommand);

        // Uygulamadan ONCE ne olacagi gorunmeli.
        Assert.True(screen.HasPreview, "Önizleme oluşmadı.");
        Assert.Equal(2, screen.TotalCount);
        Assert.Equal(2, screen.NewCount);
        Assert.Equal(0, screen.ErrorCount);
        // Onizleme sirasinda HICBIR SEY yazilmamis olmali.
        Assert.False(await InScope(db => db.Students.AnyAsync(x => x.StudentNo == "IMP-001")),
            "Önizleme veritabanına yazdı; oysa yalnızca göstermeliydi.");

        // Kullanici "İçe Aktar" butonuna basar.
        await Execute(screen.ApplyCommand);

        var stored = await InScope(db => db.Students.AsNoTracking()
            .SingleAsync(x => x.StudentNo == "IMP-001"));
        Assert.Equal("Çağrı", stored.FirstName);
        Assert.Equal("Şahinoğlu", stored.LastName);
        Assert.True(await InScope(db => db.Students.AnyAsync(x => x.StudentNo == "IMP-002")));
        Assert.NotNull(screen.Result);
        Assert.Equal(2, screen.Result!.CreatedCount);
    }

    [Fact]
    public async Task ApplyIsBlockedWhileRowsAreInvalidUnlessTheUserOptsIn()
    {
        // Ikinci satirda ad yok: sunucu bunu hatali sayar.
        var path = WriteCsv("NO;KART NO;AD;SOYAD\nIMP-010;KART-010;Geçerli;Kayit\nIMP-011;KART-011;;Eksik\n");
        var screen = NewScreen(new StubFileDialogs { OpenResult = path });
        screen.ChooseFileCommand.Execute(null);
        await Execute(screen.PreviewCommand);

        Assert.True(screen.HasErrorRows, "Hatalı satır raporlanmadı.");
        // Kullanici acikca kabul etmeden hatali dosya uygulanamaz.
        Assert.False(screen.CanApply, "Hatalı satır varken içe aktarma serbest bırakıldı.");
        Assert.False(screen.ApplyCommand.CanExecute(null), "İçe Aktar butonu hatalı dosyada aktif kaldı.");

        // Kullanici "hatalilari atla" secenegini isaretler.
        screen.ApplyValidRows = true;
        Assert.True(screen.CanApply);
        await Execute(screen.ApplyCommand);

        // Gecerli satir yazilmis, hatali satir YAZILMAMIS olmali.
        Assert.True(await InScope(db => db.Students.AnyAsync(x => x.StudentNo == "IMP-010")));
        Assert.False(await InScope(db => db.Students.AnyAsync(x => x.StudentNo == "IMP-011")),
            "Hatalı satır yine de veritabanına yazıldı.");
    }

    [Fact]
    public async Task ChoosingANewFileInvalidatesTheOldPreview()
    {
        // Kullanici A dosyasini onizleyip B dosyasini secerse, A'nin ozetiyle
        // B'yi uygulamak mumkun olmamalidir.
        var first = WriteCsv("NO;KART NO;AD;SOYAD\nIMP-020;KART-020;Ilk;Dosya\n");
        var second = WriteCsv("NO;KART NO;AD;SOYAD\nIMP-021;KART-021;Ikinci;Dosya\n");
        var dialogs = new StubFileDialogs { OpenResult = first };
        var screen = NewScreen(dialogs);
        screen.ChooseFileCommand.Execute(null);
        await Execute(screen.PreviewCommand);
        Assert.True(screen.HasPreview);

        dialogs.OpenResult = second;
        screen.ChooseFileCommand.Execute(null);

        Assert.False(screen.HasPreview, "Yeni dosya seçildi ama eski önizleme duruyor.");
        Assert.False(screen.ApplyCommand.CanExecute(null), "Önizlemesiz içe aktarma mümkün.");
    }

    [Fact]
    public async Task AFileTheServerWouldRejectIsReportedInsteadOfCrashing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sicil-bos-{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, string.Empty);
        temporaryFiles.Add(path);
        var screen = NewScreen(new StubFileDialogs { OpenResult = path });
        screen.ChooseFileCommand.Execute(null);

        await Execute(screen.PreviewCommand);

        Assert.True(screen.HasError, "Boş dosya için kullanıcıya hata gösterilmedi.");
        Assert.False(screen.HasPreview);
    }

    [Fact]
    public void WithoutWritePermissionTheScreenIsReadOnly()
    {
        var screen = new StudentImportViewModel(
            new StudentImportApiClient(client, new OperatorSession()), new StubFileDialogs(),
            ["students.read"]);

        Assert.False(screen.CanImport);
        Assert.False(screen.ChooseFileCommand.CanExecute(null));
        Assert.False(screen.PreviewCommand.CanExecute(null));
    }

    /// <summary>Butona basmayi taklit eder; kacan hata testi dusurur.</summary>
    private static async Task Execute(System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null), "Komut çalıştırılabilir değil (buton pasif).");
        Exception? escaped = null;
        void Capture(object? _, Exception error) => escaped = error;
        AsyncCommand.UnhandledError += Capture;
        try
        {
            if (command is AsyncCommand asyncCommand) await asyncCommand.ExecuteAsync(null);
            else command.Execute(null);
        }
        finally { AsyncCommand.UnhandledError -= Capture; }

        if (escaped is not null)
            Assert.Fail($"Buton komutu hata firlatti: {escaped.GetType().Name}: {escaped.Message}");
    }

    private sealed class OperatorSession : IJwtSession
    {
        public string? AccessToken { get; } = YemekhaneApiFactory.CreateOperatorToken();
        public bool IsAuthenticated => true;
    }

    /// <summary>Diyalog yerine onceden belirlenmis yolu dondurur.</summary>
    private sealed class StubFileDialogs : IFileDialogService
    {
        public string? OpenResult { get; set; }
        public string? SaveResult { get; set; }
        public string? OpenFile(string title, string filter) => OpenResult;
        public string? SaveFile(string title, string filter, string suggestedFileName) => SaveResult;
    }
}
