using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Parents;
using Yemekhane.Application.Students;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Parents;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Students;

namespace Yemekhane.UnitTests.Students;

/// <summary>
/// Ogrenci numarasi ve veli adi ISTEGE BAGLIDIR. Okul bazi ogrenciye numara vermiyor
/// (anasinifi, misafir) ve cogu velinin elinde yalnizca telefon var. Kimlik KART
/// numarasindan, SMS ise VELI TELEFONUNDAN saglanir; bu ikisi zorunlu kalir.
/// </summary>
public sealed class OptionalIdentityFieldsTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public OptionalIdentityFieldsTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private StudentService Students() => new(new EfStudentRepository(db));
    private ParentService Parents() => new(new EfParentRepository(db));

    private static SaveStudentRequest Student(string? no, string first = "Ayşe", string last = "Yılmaz") =>
        new(no ?? "", first, last);

    [Fact]
    public async Task StudentCanBeSavedWithoutANumber()
    {
        var created = await Students().CreateAsync(Student(""));

        Assert.Equal("", created.StudentNo);
        Assert.Equal("Ayşe", created.FirstName);
    }

    /// <summary>Numarasiz IKI ogrenci kaydedilebilmeli; benzersiz indeks bunu engelliyordu.</summary>
    [Fact]
    public async Task TwoStudentsWithoutANumberDoNotCollide()
    {
        await Students().CreateAsync(Student("", "Ayşe", "Yılmaz"));
        await Students().CreateAsync(Student("", "Mehmet", "Demir"));

        Assert.Equal(2, await db.Students.CountAsync());
    }

    [Fact]
    public async Task WhitespaceNumberIsStoredAsEmpty()
    {
        var created = await Students().CreateAsync(Student("   "));

        Assert.Equal("", created.StudentNo);
    }

    /// <summary>Numara VERILDIYSE hala benzersizdir; ayni numara iki ogrenciye verilemez.</summary>
    [Fact]
    public async Task ANumberThatIsGivenStaysUnique()
    {
        await Students().CreateAsync(Student("5012"));

        var error = await Assert.ThrowsAsync<EntityConflictException>(() => Students().CreateAsync(Student("5012", "Mehmet")));

        Assert.Contains("5012", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NumberLengthIsStillCapped()
    {
        var error = await Assert.ThrowsAsync<RequestValidationException>(
            () => Students().CreateAsync(Student(new string('9', 33))));

        Assert.Equal("Öğrenci NO alanı en fazla 32 karakter olabilir.", error.Message);
    }

    /// <summary>Ad ve soyad ZORUNLU kalir: numarasiz ogrencinin tek ayirt edici alani odur.</summary>
    [Fact]
    public async Task NameIsStillRequired()
    {
        await Assert.ThrowsAsync<RequestValidationException>(() => Students().CreateAsync(Student("5012", first: "")));
        await Assert.ThrowsAsync<RequestValidationException>(() => Students().CreateAsync(Student("5012", last: "")));
    }

    [Fact]
    public async Task ParentCanBeSavedWithoutAName()
    {
        var studentId = (await Students().CreateAsync(Student("5012"))).Id;

        var parent = await Parents().CreateAsync(studentId, new SaveParentRequest("", "5321234567"));

        Assert.Equal("", parent.Name);
        Assert.Equal("+905321234567", parent.Phone);
    }

    /// <summary>Telefon ZORUNLU kalir: SMS ve kayit telefonla calisir, adsiz veli ise anlamlidir.</summary>
    [Fact]
    public async Task ParentPhoneIsStillRequired()
    {
        var studentId = (await Students().CreateAsync(Student("5012"))).Id;

        await Assert.ThrowsAsync<RequestValidationException>(
            () => Parents().CreateAsync(studentId, new SaveParentRequest("Fatma Yılmaz", "")));
    }

    [Fact]
    public async Task ParentNameLengthIsStillCapped()
    {
        var studentId = (await Students().CreateAsync(Student("5012"))).Id;

        var error = await Assert.ThrowsAsync<RequestValidationException>(
            () => Parents().CreateAsync(studentId, new SaveParentRequest(new string('A', 201), "5321234567")));

        Assert.Equal("Veli adı en fazla 200 karakter olabilir.", error.Message);
    }

    /// <summary>Numarasiz ogrenci ada gore hala bulunabilmeli; arama numaraya bagli degildir.</summary>
    [Fact]
    public async Task NumberlessStudentIsStillSearchableByName()
    {
        await Students().CreateAsync(Student("", "Ayşe", "Yılmaz"));

        var result = await new EfStudentRepository(db)
            .SearchAsync(new StudentQuery(Search: "ayşe"), default);

        Assert.Single(result.Items);
        Assert.Equal("Ayşe", result.Items[0].FirstName);
    }

    /// <summary>Numaraya gore arama BOS numarayi getirmemeli; filtre yok sayilmamali.</summary>
    [Fact]
    public async Task SearchingByNumberDoesNotMatchNumberlessStudents()
    {
        await Students().CreateAsync(Student("", "Ayşe", "Yılmaz"));
        await Students().CreateAsync(Student("5012", "Mehmet", "Demir"));

        var result = await new EfStudentRepository(db)
            .SearchAsync(new StudentQuery(StudentNo: "5012"), default);

        Assert.Single(result.Items);
        Assert.Equal("5012", result.Items[0].StudentNo);
    }
}
