using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Yemekhane.Api.Authentication;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Reports;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Api;

/// <summary>
/// Hassas veri AYNI kullanici icin TUM uclarda ayni sekilde maskelenmelidir.
///
/// Tehlike: bir uc maskeliyor, digeri maskelemiyorsa izin sistemi kagit uzerinde
/// dogru gorunur ama pratikte atlanabilir. Kullanici maskeli ucu birakip maskesiz
/// olani cagirir ve ayni veriyi acik alir.
/// </summary>
public sealed class SensitiveDataLeakTests : IClassFixture<YemekhaneApiFactory>
{
    private readonly YemekhaneApiFactory factory;

    public SensitiveDataLeakTests(YemekhaneApiFactory factory) => this.factory = factory;

    /// <summary>
    /// students.sensitive.read IZNI OLMAYAN kullanici, veli telefonunu HICBIR uctan
    /// acik goremmelidir.
    ///
    /// Sicil listesi raporu ile ogrenci listesi ayni veriyi tasir: biri maskeliyorsa
    /// digeri de maskelemelidir. Aksi halde reports.export izni, students.sensitive.read
    /// kapisini sessizce atlar ve butun okulun veli telefonlari tek dosyada disari cikar.
    /// </summary>
    [Fact]
    public async Task HassasIzniOlmayanVeliTelefonunuRapordanDaGoremez()
    {
        var (studentId, phone) = await SeedStudentWithParentAsync();

        using var client = await CreateClientAsync(
            Permissions.StudentsRead, Permissions.ReportsRead, Permissions.ReportsExport);

        // 1) Ogrenci listesi: maskeli gelmelidir (mevcut davranis).
        var list = await client.GetFromJsonAsync<StudentListResponse>("/api/students?pageSize=200");
        var listed = list!.Items.SingleOrDefault(x => x.Id == studentId);
        Assert.NotNull(listed);
        Assert.DoesNotContain(phone, listed!.ParentPhone ?? "", StringComparison.Ordinal);

        // 2) Sicil listesi raporu: AYNI kullanici, AYNI veri -> yine maskeli olmalidir.
        var report = await client.GetFromJsonAsync<ReportPage>(
            $"/api/reports/{ReportType.StudentList}?page=1&pageSize=200");
        var row = report!.Items.SingleOrDefault(x => x.StudentNo == StudentNo);
        Assert.NotNull(row);
        Assert.False(string.Equals(row!.ParentPhone, phone, StringComparison.Ordinal),
            $"Veli telefonu rapor ucundan MASKESIZ sizdi: {row.ParentPhone}. " +
            "Ayni kullanici /api/students ucunda maskeli goruyor.");
    }

    /// <summary>
    /// Disa aktarma (CSV) ayni kurala tabidir: dosyaya yazilan icerik ekranda
    /// gosterilenden daha acik OLAMAZ. Rapor CSV'si dogrudan indirilip paylasilabildigi
    /// icin sizinti burada daha da kalicidir.
    /// </summary>
    [Fact]
    public async Task HassasIzniOlmayaninIndirdigiCsvVeliTelefonuIcermez()
    {
        var (_, phone) = await SeedStudentWithParentAsync();

        using var client = await CreateClientAsync(
            Permissions.StudentsRead, Permissions.ReportsRead, Permissions.ReportsExport);

        var csv = await client.GetStringAsync($"/api/reports/{ReportType.StudentList}/csv");

        Assert.DoesNotContain(phone, csv, StringComparison.Ordinal);
    }

    /// <summary>
    /// KARSI KONTROL: hassas izni OLAN kullanici veli telefonunu TAM gormelidir.
    /// Maskeleme duzeltmesi asiri olmamalidir -- okul yoneticisi veliyi arayabilmelidir.
    /// </summary>
    [Fact]
    public async Task HassasIzniOlanVeliTelefonunuTamGorur()
    {
        var (_, phone) = await SeedStudentWithParentAsync();

        using var client = await CreateClientAsync(
            Permissions.StudentsRead, Permissions.ReportsRead, Permissions.ReportsExport,
            Permissions.StudentsSensitiveRead);

        var report = await client.GetFromJsonAsync<ReportPage>(
            $"/api/reports/{ReportType.StudentList}?page=1&pageSize=200");
        var row = report!.Items.SingleOrDefault(x => x.StudentNo == StudentNo);
        Assert.NotNull(row);
        Assert.Equal(phone, row!.ParentPhone);

        var csv = await client.GetStringAsync($"/api/reports/{ReportType.StudentList}/csv");
        Assert.Contains(phone, csv, StringComparison.Ordinal);
    }

    private const string StudentNo = "SIZINTI-TEST-1";
    private const string ParentPhone = "+905551234567";

    private async Task<(Guid StudentId, string Phone)> SeedStudentWithParentAsync()
    {
        _ = factory.Server;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();

        var existing = db.Students.FirstOrDefault(x => x.StudentNo == StudentNo);
        if (existing is not null) return (existing.Id, ParentPhone);

        var student = new Student { StudentNo = StudentNo, FirstName = "Sızıntı", LastName = "Testi" };
        db.Students.Add(student);
        db.Add(new Parent
        {
            StudentId = student.Id, Name = "Veli Testi",
            NormalizedPhone = ParentPhone, IsPrimary = true, IsActive = true
        });
        await db.SaveChangesAsync();
        return (student.Id, ParentPhone);
    }

    private async Task<HttpClient> CreateClientAsync(params string[] permissions)
    {
        _ = factory.Server;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
        var username = $"sizinti-{Guid.NewGuid():N}";
        var user = new User
        {
            Id = Guid.NewGuid(), Username = username,
            NormalizedUsername = LoginService.NormalizeUsername(username),
            PasswordHash = string.Empty, SecurityStamp = Guid.NewGuid().ToString("N"),
            IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        user.PasswordHash = hasher.HashPassword(user, "Strong leak password!");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Username),
            new("security_stamp", user.SecurityStamp)
        };
        claims.AddRange(permissions.Select(x => new Claim(Permissions.ClaimType, x)));
        var token = new JwtSecurityToken("yemekhane-test", "yemekhane-test", claims,
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(YemekhaneApiFactory.SigningKey)),
                SecurityAlgorithms.HmacSha256));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed record StudentListResponse(IReadOnlyList<StudentListItem> Items);
    private sealed record StudentListItem(Guid Id, string StudentNo, string? ParentPhone);
    private sealed record ReportPage(IReadOnlyList<ReportRowDto> Items);
    private sealed record ReportRowDto(string? StudentNo, string? ParentPhone);
}
