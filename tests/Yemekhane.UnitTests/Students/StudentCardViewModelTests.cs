using System.Net;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Application.Leaves;
using Yemekhane.Application.Organization;
using Yemekhane.Application.Students;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;

namespace Yemekhane.UnitTests.Students;

/// <summary>
/// Ogrenci Karti cekmecesi (eski programdaki Sicil Karti): formdaki HER alan istege gider,
/// TC 11 hane dogrulanir, "+" ile eklenen tanim listeye girip SECILIR, fotograf secilir /
/// kaldirilir ve kayitta sunucuya gider. Istemci sahtedir; API ayri test edilir.
/// </summary>
public sealed class StudentCardViewModelTests
{
    /// <summary>Formdaki her alan (tarih, dort tanim, parmak izi, PI ID, adres, not) istekte AYNEN yer alir.</summary>
    [Fact]
    public async Task FormdakiHerAlanIstegeGider()
    {
        var api = new FakeApi();
        using var vm = Create(api, "students.write");
        vm.NewStudentCommand.Execute(null);
        await Until(() => vm.FormClass.IsLoaded && vm.FormJob.IsLoaded);

        vm.FormStudentNo = "7001"; vm.FormFirstName = "Ada"; vm.FormLastName = "Yılmaz";
        vm.FormNationalId = "12345678901"; vm.FormBirthDate = new DateTime(2014, 5, 1);
        vm.FormClass.Select(api.Class5A.Id); vm.FormSection.Select(api.SectionB.Id);
        vm.FormDepartment.Select(api.DeptSayisal.Id); vm.FormJob.Select(api.JobOgrenci.Id);
        vm.FormFingerprintId = "FP-7"; vm.FormPid = "PI-7"; vm.FormAddress = "Atatürk Cad. 1"; vm.FormNotes = "not";
        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.SaveCount == 1);

        var r = api.LastSaveRequest!;
        Assert.Equal("7001", r.StudentNo); Assert.Equal("Ada", r.FirstName); Assert.Equal("Yılmaz", r.LastName);
        Assert.Equal("12345678901", r.NationalId); Assert.Equal(new DateOnly(2014, 5, 1), r.BirthDate);
        Assert.Equal(api.Class5A.Id, r.ClassId); Assert.Equal(api.SectionB.Id, r.SectionId);
        Assert.Equal(api.DeptSayisal.Id, r.DepartmentId); Assert.Equal(api.JobOgrenci.Id, r.JobId);
        Assert.Equal("FP-7", r.FingerprintId); Assert.Equal("PI-7", r.Pid); Assert.Equal("Atatürk Cad. 1", r.Address); Assert.Equal("not", r.Notes);
        Assert.True(r.IsActive);
    }

    /// <summary>"Seçiniz" yer tutucusu sunucuya null gider; bos tanimlar kaydi engellemez.</summary>
    [Fact]
    public async Task SeciniziBosBirakmakNullGonderir()
    {
        var api = new FakeApi();
        using var vm = Create(api, "students.write");
        vm.NewStudentCommand.Execute(null);
        await Until(() => vm.FormClass.IsLoaded);
        vm.FormStudentNo = "7002"; vm.FormFirstName = "Ali"; vm.FormLastName = "Demir";
        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.SaveCount == 1);
        Assert.Null(api.LastSaveRequest!.ClassId); Assert.Null(api.LastSaveRequest.SectionId);
        Assert.Null(api.LastSaveRequest.DepartmentId); Assert.Null(api.LastSaveRequest.JobId);
        Assert.Null(api.LastSaveRequest.BirthDate);
    }

    [Theory]
    [InlineData("1234567890")]
    [InlineData("123456789012")]
    [InlineData("1234567890A")]
    public async Task TcKimlikOnBirRakamDegilseKaydetmez(string nationalId)
    {
        var api = new FakeApi();
        using var vm = Create(api, "students.write");
        vm.NewStudentCommand.Execute(null);
        vm.FormStudentNo = "7003"; vm.FormFirstName = "Ali"; vm.FormLastName = "Demir"; vm.FormNationalId = nationalId;
        vm.SaveStudentCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(0, api.SaveCount);
        Assert.Contains("11 rakam", vm.ErrorMessage);
        Assert.True(vm.IsFormOpen, "hatada form acik kalmali");
    }

    [Fact]
    public async Task BosTcKabulEdilirGelecekDogumTarihiReddedilir()
    {
        var api = new FakeApi();
        using var vm = Create(api, "students.write");
        vm.NewStudentCommand.Execute(null);
        vm.FormStudentNo = "7004"; vm.FormFirstName = "Ali"; vm.FormLastName = "Demir"; vm.FormNationalId = "";
        vm.FormBirthDate = DateTime.Today.AddDays(1);
        vm.SaveStudentCommand.Execute(null);
        await Task.Delay(50);
        Assert.Equal(0, api.SaveCount);
        Assert.Contains("Doğum tarihi", vm.ErrorMessage);
        vm.FormBirthDate = null;
        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.SaveCount == 1);
        Assert.Null(api.LastSaveRequest!.NationalId);
    }

    /// <summary>Duzenle: cekmece Details'teki tum alanlarla dolar (tanim secimleri dahil).</summary>
    [Fact]
    public async Task DuzenleFormuDetaydanDoldurur()
    {
        var api = new FakeApi();
        using var vm = Create(api, "students.write");
        api.SetDetails(Details() with
        {
            NationalId = "98765432109", BirthDate = new DateOnly(2013, 2, 3), ClassId = api.Class5A.Id, SectionId = api.SectionB.Id,
            DepartmentId = api.DeptSayisal.Id, JobId = api.JobOgrenci.Id, FingerprintId = "FP", Pid = "PI", Address = "Adres", Notes = "n"
        });
        vm.OpenFullDetailCommand.Execute(Row());
        await Until(() => vm.Details is not null);
        vm.EditStudentCommand.Execute(null);
        await Until(() => vm.FormClass.IsLoaded && vm.FormClass.SelectedId == api.Class5A.Id);

        Assert.True(vm.IsFormOpen);
        Assert.Equal("98765432109", vm.FormNationalId);
        Assert.Equal(new DateTime(2013, 2, 3), vm.FormBirthDate);
        Assert.Equal(api.SectionB.Id, vm.FormSection.SelectedId);
        Assert.Equal(api.DeptSayisal.Id, vm.FormDepartment.SelectedId);
        Assert.Equal(api.JobOgrenci.Id, vm.FormJob.SelectedId);
        Assert.Equal("FP", vm.FormFingerprintId); Assert.Equal("PI", vm.FormPid); Assert.Equal("Adres", vm.FormAddress);
        Assert.Equal("Sayısal", vm.DetailDepartmentName); Assert.Equal("Öğrenci", vm.DetailJobName);

        // Hicbir alana dokunmadan Kaydet: istek Details ile birebir (hicbir alan kaybolmaz).
        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.SaveCount == 1);
        var r = api.LastSaveRequest!;
        Assert.Equal(api.Class5A.Id, r.ClassId); Assert.Equal(api.SectionB.Id, r.SectionId);
        Assert.Equal(api.DeptSayisal.Id, r.DepartmentId); Assert.Equal(api.JobOgrenci.Id, r.JobId);
        Assert.Equal(new DateOnly(2013, 2, 3), r.BirthDate); Assert.Equal("FP", r.FingerprintId); Assert.Equal("PI", r.Pid);
    }

    /// <summary>"+" ile eklenen sube sunucuya POST edilir, listeye girer ve SECILI olur; kayit onu gonderir.</summary>
    [Fact]
    public async Task ArtiIleEklenenTanimSecilir()
    {
        var api = new FakeApi();
        using var vm = Create(api, "students.write");
        vm.NewStudentCommand.Execute(null);
        await Until(() => vm.FormSection.IsLoaded);

        vm.FormSection.OpenAddCommand.Execute(null);
        Assert.True(vm.FormSection.IsAdding);
        vm.FormSection.NewName = " F ";
        vm.FormSection.AddCommand.Execute(null);
        await Until(() => !vm.FormSection.IsAdding);

        Assert.Equal((LookupKind.Section, "F"), api.LastCreatedLookup);
        Assert.Contains(vm.FormSection.Items, x => x.Name == "F");
        Assert.Equal("F", vm.FormSection.Selected!.Name);
        Assert.Null(vm.FormSection.Error);

        vm.FormStudentNo = "7005"; vm.FormFirstName = "Ali"; vm.FormLastName = "Demir";
        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.SaveCount == 1);
        Assert.Equal(vm.FormSection.Selected.Id, api.LastSaveRequest!.SectionId);
    }

    /// <summary>Sinif ucu ayri sozlesmeye sahiptir; "+" ile sinif eklemek de calisir ve secilir.</summary>
    [Fact]
    public async Task ArtiIleSinifEklenir()
    {
        var api = new FakeApi();
        using var vm = Create(api, "students.write");
        vm.NewStudentCommand.Execute(null);
        await Until(() => vm.FormClass.IsLoaded);
        vm.FormClass.OpenAddCommand.Execute(null);
        vm.FormClass.NewName = "9Z";
        vm.FormClass.AddCommand.Execute(null);
        await Until(() => !vm.FormClass.IsAdding);
        Assert.Equal((LookupKind.Class, "9Z"), api.LastCreatedLookup);
        Assert.Equal("9Z", vm.FormClass.Selected!.Name);
        Assert.NotEqual(Guid.Empty, vm.FormClass.SelectedId);
    }

    /// <summary>Cakismada (409) sunucu mesaji kutunun altinda AYNEN gorunur; kutu acik kalir, secim degismez.</summary>
    [Fact]
    public async Task TanimCakismasiSunucuMesajiniGosterir()
    {
        var api = new FakeApi { CreateLookupFailure = new ApiRequestException("Şube adı zaten kayıtlı.", HttpStatusCode.Conflict) };
        using var vm = Create(api, "students.write");
        vm.NewStudentCommand.Execute(null);
        await Until(() => vm.FormSection.IsLoaded);
        vm.FormSection.OpenAddCommand.Execute(null);
        vm.FormSection.NewName = "B";
        vm.FormSection.AddCommand.Execute(null);
        await Until(() => vm.FormSection.HasError);
        Assert.Equal("Şube adı zaten kayıtlı.", vm.FormSection.Error);
        Assert.True(vm.FormSection.IsAdding);
        Assert.Null(vm.FormSection.SelectedId);
    }

    [Fact]
    public async Task BosTanimAdiEklenmez()
    {
        var api = new FakeApi();
        using var vm = Create(api, "students.write");
        vm.NewStudentCommand.Execute(null);
        await Until(() => vm.FormJob.IsLoaded);
        vm.FormJob.OpenAddCommand.Execute(null);
        vm.FormJob.NewName = "   ";
        vm.FormJob.AddCommand.Execute(null);
        await Until(() => vm.FormJob.HasError);
        Assert.Null(api.LastCreatedLookup);
        Assert.Contains("boş olamaz", vm.FormJob.Error);
    }

    /// <summary>
    /// Fotograf: "Resim Sec" diyalogdan dosya alir, onizleme dolar; Kaydet'te dosya
    /// KAYDEDILEN ogrencinin kimligiyle yuklenir (yeni ogrencide kimlik ancak kayitta dogar).
    /// </summary>
    [Fact]
    public async Task FotografSecilirVeKayittaYuklenir()
    {
        var png = TestPng.Create();
        var path = Path.Combine(Path.GetTempPath(), $"yp-photo-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, png);
        try
        {
            var api = new FakeApi();
            var dialog = new FakeDialog(path);
            using var vm = new StudentsViewModel(api, new ShellNavigationService([ShellRoutes.Students]), ["students.write"], fileDialog: dialog);
            vm.NewStudentCommand.Execute(null);
            Assert.False(vm.HasPhoto);
            Assert.True(vm.SelectPhotoCommand.CanExecute(null), "form acikken Resim Sec etkin olmali");

            vm.SelectPhotoCommand.Execute(null);
            Assert.True(vm.HasPhoto, "secimden sonra onizleme dolmali");
            Assert.NotNull(vm.PhotoImage);
            Assert.Equal(Path.GetFileName(path), vm.PendingPhotoName);
            Assert.Equal(0, api.UploadCount);

            vm.FormStudentNo = "7006"; vm.FormFirstName = "Ali"; vm.FormLastName = "Demir";
            vm.SaveStudentCommand.Execute(null);
            await Until(() => api.UploadCount == 1 && !vm.IsFormOpen);
            Assert.Equal(api.Details.Id, api.LastUploadId);
            Assert.Equal(png, api.LastUploadBytes);
            Assert.Null(vm.PendingPhotoName);
            Assert.False(vm.HasError, vm.ErrorMessage);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task FotografKaldirKayittaSiler()
    {
        var api = new FakeApi { PhotoBytes = TestPng.Create() };
        using var vm = Create(api, "students.write");
        api.SetDetails(Details() with { PhotoPath = "photos/x.png" });
        vm.OpenFullDetailCommand.Execute(Row());
        await Until(() => vm.HasPhoto);
        vm.EditStudentCommand.Execute(null);
        Assert.True(vm.RemovePhotoCommand.CanExecute(null));
        vm.RemovePhotoCommand.Execute(null);
        Assert.False(vm.HasPhoto);
        Assert.Equal(0, api.DeletePhotoCount);

        vm.SaveStudentCommand.Execute(null);
        await Until(() => api.DeletePhotoCount == 1);
        Assert.Equal(0, api.UploadCount);
    }

    /// <summary>Iptal: secilen fotograf atilir, onizleme sunucudaki haline (fotografsiz) doner; hicbir istek gitmez.</summary>
    [Fact]
    public async Task IptalSecilenFotografiAtar()
    {
        var png = TestPng.Create();
        var path = Path.Combine(Path.GetTempPath(), $"yp-photo-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, png);
        try
        {
            var api = new FakeApi();
            using var vm = new StudentsViewModel(api, new ShellNavigationService([ShellRoutes.Students]), ["students.write"], fileDialog: new FakeDialog(path));
            vm.OpenFullDetailCommand.Execute(Row());
            await Until(() => vm.Details is not null);
            vm.EditStudentCommand.Execute(null);
            vm.SelectPhotoCommand.Execute(null);
            Assert.True(vm.HasPhoto);
            vm.CancelEditCommand.Execute(null);
            Assert.False(vm.HasPhoto);
            Assert.Null(vm.PendingPhotoName);
            Assert.Equal(0, api.UploadCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FotografDosyaTuruVeBoyutuDenetlenir()
    {
        var txt = Path.Combine(Path.GetTempPath(), $"yp-photo-{Guid.NewGuid():N}.txt");
        var big = Path.Combine(Path.GetTempPath(), $"yp-photo-{Guid.NewGuid():N}.png");
        File.WriteAllText(txt, "metin");
        File.WriteAllBytes(big, new byte[StudentPhotoService.MaximumBytes + 1]);
        try
        {
            using var vm = Create(new FakeApi(), "students.write");
            vm.NewStudentCommand.Execute(null);
            vm.StagePhoto(txt);
            Assert.False(vm.HasPhoto); Assert.Contains("JPG ve PNG", vm.PhotoError);
            vm.StagePhoto(big);
            Assert.False(vm.HasPhoto); Assert.Contains("2 MB", vm.PhotoError);
        }
        finally { File.Delete(txt); File.Delete(big); }
    }

    /// <summary>Detay acilinca sunucudaki fotograf indirilip onizlemeye konur; fotografsiz kayitta indirme YAPILMAZ.</summary>
    [Fact]
    public async Task DetayAcilincaFotografIndirilir()
    {
        var api = new FakeApi { PhotoBytes = TestPng.Create() };
        using var vm = Create(api);
        vm.OpenFullDetailCommand.Execute(Row());
        await Until(() => vm.Details is not null);
        await Task.Delay(50);
        Assert.Equal(0, api.DownloadCount);
        Assert.False(vm.HasPhoto);

        api.SetDetails(Details() with { PhotoPath = "photos/x.png" });
        vm.OpenFullDetailCommand.Execute(Row());
        await Until(() => vm.HasPhoto);
        Assert.Equal(1, api.DownloadCount);
    }

    /// <summary>Pasiflestir formu acmaz; kayit Details'ten aynen (sinif/bolum dahil) yeniden yazilir.</summary>
    [Fact]
    public async Task PasiflestirDetaydakiAlanlariKorur()
    {
        var api = new FakeApi();
        using var vm = Create(api, "students.write");
        api.SetDetails(Details() with { ClassId = api.Class5A.Id, DepartmentId = api.DeptSayisal.Id, JobId = api.JobOgrenci.Id, Pid = "PI" });
        vm.OpenFullDetailCommand.Execute(Row());
        await Until(() => vm.Details is not null);
        vm.DeactivateCommand.Execute(null);
        await Until(() => api.SaveCount == 1);
        Assert.False(api.LastSaveRequest!.IsActive);
        Assert.Equal(api.Class5A.Id, api.LastSaveRequest.ClassId);
        Assert.Equal(api.DeptSayisal.Id, api.LastSaveRequest.DepartmentId);
        Assert.Equal(api.JobOgrenci.Id, api.LastSaveRequest.JobId);
        Assert.Equal("PI", api.LastSaveRequest.Pid);
    }

    // ---------------------------------------------------------------- yardimcilar

    private static StudentsViewModel Create(FakeApi api, params string[] permissions) =>
        new(api, new ShellNavigationService([ShellRoutes.Students, ShellRoutes.StudentDetail]), permissions, fileDialog: new FakeDialog(null));
    private static StudentListItem Row() => new(Guid.NewGuid(), "42", "CARD42", "Ada", "Yılmaz", "5", "A", "Sayısal", "+905551234567", true, 1, true, DateTimeOffset.UtcNow);
    private static StudentDetails Details() => new(Guid.NewGuid(), "42", null, "Ada", "Yılmaz", null, null, null, null, null, null, null, null, null, null, true, new DateOnly(2026, 8, 31));
    private static async Task Until(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < timeout) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeDialog(string? path) : IFileDialogService
    {
        public string? OpenFile(string title, string filter) => path;
        public string? SaveFile(string title, string filter, string suggestedFileName) => null;
    }

    private sealed class FakeApi : IStudentApiClient
    {
        public readonly LookupRecord Class5A = new(Guid.NewGuid(), "5A", 3);
        public readonly LookupRecord SectionB = new(Guid.NewGuid(), "B", 3);
        public readonly LookupRecord DeptSayisal = new(Guid.NewGuid(), "Sayısal", 3);
        public readonly LookupRecord JobOgrenci = new(Guid.NewGuid(), "Öğrenci", 3);
        public int SaveCount, UploadCount, DownloadCount, DeletePhotoCount;
        public SaveStudentRequest? LastSaveRequest;
        public (LookupKind Kind, string Name)? LastCreatedLookup;
        public Exception? CreateLookupFailure;
        public byte[]? PhotoBytes, LastUploadBytes;
        public Guid? LastUploadId;
        public StudentDetails Details { get; private set; } = StudentCardViewModelTests.Details();
        public void SetDetails(StudentDetails value) => Details = value;

        public Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PagedResult<StudentListItem>([Row() with { Id = Details.Id }], 1, 50, 1));
        public Task<StudentDetails> GetAsync(Guid id, CancellationToken cancellationToken = default) { Details = Details with { Id = id }; return Task.FromResult(Details); }
        public Task<StudentDetails> SaveAsync(Guid? id, SaveStudentRequest request, CancellationToken cancellationToken = default)
        {
            SaveCount++; LastSaveRequest = request;
            Details = Details with
            {
                Id = id ?? Details.Id, StudentNo = request.StudentNo, FirstName = request.FirstName, LastName = request.LastName, NationalId = request.NationalId,
                BirthDate = request.BirthDate, ClassId = request.ClassId, SectionId = request.SectionId, DepartmentId = request.DepartmentId, JobId = request.JobId,
                FingerprintId = request.FingerprintId, Pid = request.Pid, Address = request.Address, Notes = request.Notes, IsActive = request.IsActive
            };
            return Task.FromResult(Details);
        }
        public Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<object>> LoadTabAsync(string tab, Guid studentId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<object>>([]);
        public Task GiveLeaveAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ReplaceCardAsync(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LookupRecord>> GetLookupsAsync(LookupKind kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LookupRecord>>(kind switch
            {
                LookupKind.Class => [Class5A, new(Guid.NewGuid(), "6A", 1)],
                LookupKind.Section => [new(Guid.NewGuid(), "A", 1), SectionB],
                LookupKind.Department => [DeptSayisal],
                _ => [JobOgrenci]
            });
        public Task<LookupRecord> CreateLookupAsync(LookupKind kind, string name, CancellationToken cancellationToken = default)
        {
            if (CreateLookupFailure is not null) return Task.FromException<LookupRecord>(CreateLookupFailure);
            LastCreatedLookup = (kind, name);
            return Task.FromResult(new LookupRecord(Guid.NewGuid(), name, 0));
        }
        public Task<StudentDetails> UploadPhotoAsync(Guid studentId, string fileName, byte[] content, CancellationToken cancellationToken = default)
        {
            UploadCount++; LastUploadId = studentId; LastUploadBytes = content; PhotoBytes = content;
            Details = Details with { PhotoPath = "photos/" + studentId.ToString("D") + ".png" };
            return Task.FromResult(Details);
        }
        public Task<byte[]?> DownloadPhotoAsync(Guid studentId, CancellationToken cancellationToken = default) { DownloadCount++; return Task.FromResult(PhotoBytes); }
        public Task DeletePhotoAsync(Guid studentId, CancellationToken cancellationToken = default)
        {
            DeletePhotoCount++; PhotoBytes = null; Details = Details with { PhotoPath = null }; return Task.CompletedTask;
        }
    }
}

/// <summary>Testler icin gecerli, kucuk (8x8, kirmizi) bir PNG uretir; harici dosya gerekmez.</summary>
public static class TestPng
{
    public static byte[] Create(int size = 8)
    {
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var visual = new System.Windows.Media.DrawingVisual();
        using (var context = visual.RenderOpen())
            context.DrawRectangle(System.Windows.Media.Brushes.OrangeRed, null, new System.Windows.Rect(0, 0, size, size));
        bitmap.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
