using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.StudentImports;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.StudentImports;

namespace Yemekhane.UnitTests.StudentImports;

public sealed class StudentImportServiceTests
{
    private static readonly Guid ActorId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    /// <summary>
    /// C#'ta % kalan operatorudur; cikarma negatifse sonuc da negatif olur ve hicbir rakama esit olamaz.
    /// Asagidaki TC kimlik numaralari checksum'a gore GECERLIDIR ve kabul edilmelidir.
    /// </summary>
    [Theory]
    [InlineData("13180804016")]
    [InlineData("19020909088")]
    [InlineData("29091809064")]
    public async Task ChecksumValidNationalIdsWithNegativeIntermediateAreAccepted(string nationalId)
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var csv = "NO;KART NO;AD;SOYAD;TC;TELEFON\r\n" +
                  $"9001;KART-9001;Ada;Yılmaz;{nationalId};05321112233\r\n";

        var preview = await fixture.PreviewCsv(csv);

        Assert.DoesNotContain(preview.Rows.SelectMany(x => x.Errors), error => error.Code == "InvalidNationalId");
    }

    [Fact]
    public async Task ChecksumInvalidNationalIdIsStillRejected()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var csv = "NO;KART NO;AD;SOYAD;TC;TELEFON\r\n" +
                  "9002;KART-9002;Ali;Demir;13180804017;05321112234\r\n";

        var preview = await fixture.PreviewCsv(csv);

        Assert.Contains(preview.Rows.SelectMany(x => x.Errors), error => error.Code == "InvalidNationalId");
    }

    [Fact]
    public async Task CsvPreviewHandlesBomTurkishTextSeparatedNumbersDuplicatesAndErrorReport()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var csv = "\uFEFFNO;KART NO;AD;SOYAD;TC;TELEFON\r\n" +
                  "6811;8222704;Çağrı;Şahin;10000000146;05327776321\r\n" +
                  "6811;9000;İpek;Öztürk;;123\r\n";

        var preview = await fixture.PreviewCsv(csv);

        Assert.Equal(2, preview.TotalCount);
        Assert.Equal(0, preview.NewCount);
        Assert.Equal(0, preview.UpdateCount);
        Assert.Equal(2, preview.ErrorCount);
        Assert.Equal("6811", preview.Rows[0].StudentNo);
        Assert.Equal("8222704", preview.Rows[0].CardNumber);
        Assert.Contains(preview.Rows[0].Errors, x => x.Code == "DuplicateStudentNo");
        Assert.Contains(preview.Rows[1].Errors, x => x.Code == "InvalidPhone");
        var report = fixture.Service.GetErrorReport(preview.Token, ActorId);
        var reportText = Encoding.UTF8.GetString(report.Content);
        Assert.StartsWith("\uFEFFSatır;NO;KART NO", reportText);
        Assert.Contains("DuplicateStudentNo", reportText);
        Assert.EndsWith(".csv", report.FileName);
    }

    [Fact]
    public async Task XlsxApplyUpdatesStudentAndPreservesCardHistory()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var oldStudent = new Student { StudentNo = "10", FirstName = "Eski", LastName = "Ad" };
        fixture.Db.Students.Add(oldStudent);
        fixture.Db.StudentCards.Add(new StudentCard { StudentId = oldStudent.Id, CardNumber = "OLD", ValidFrom = DateTimeOffset.UtcNow.AddDays(-1) });
        await fixture.Db.SaveChangesAsync();
        await using var workbook = CreateXlsx([
            ["NO", "KART NO", "AD", "SOYAD"],
            ["10", "NEW", "Özgür", "Işık"],
            ["11", "CARD11", "İpek", "Çetin"]
        ]);

        var preview = await fixture.Service.PreviewAsync(workbook, "students.xlsx", ActorId);
        var result = await fixture.Service.ApplyAsync(new(preview.Token), ActorId);

        Assert.Equal(1, preview.NewCount);
        Assert.Equal(1, preview.UpdateCount);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.UpdatedCount);
        var cards = (await fixture.Db.StudentCards.Where(x => x.StudentId == oldStudent.Id).ToListAsync()).OrderBy(x => x.ValidFrom).ToList();
        Assert.Equal(2, cards.Count);
        Assert.False(cards[0].IsActive);
        Assert.NotNull(cards[0].ValidTo);
        Assert.Equal("Sicil importu ile kart değişimi", cards[0].ReplacementReason);
        Assert.True(cards[1].IsActive);
        Assert.Equal("NEW", cards[1].CardNumber);
    }

    [Fact]
    public async Task ChangedPreviewAndReplayAreRejected()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var changed = await fixture.PreviewCsv("NO,KART NO,AD,SOYAD\n20,C20,Ayşe,Yılmaz");
        fixture.Db.Students.Add(new Student { StudentNo = "20", FirstName = "Başka", LastName = "Kayıt" });
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<EntityConflictException>(() => fixture.Service.ApplyAsync(new(changed.Token), ActorId));

        var once = await fixture.PreviewCsv("NO;KART NO;AD;SOYAD\n21;C21;Can;Şen");
        await fixture.Service.ApplyAsync(new(once.Token), ActorId);
        await Assert.ThrowsAsync<EntityConflictException>(() => fixture.Service.ApplyAsync(new(once.Token), ActorId));
    }

    [Fact]
    public async Task ApplyRollsBackEveryValidRowWhenAnyDatabaseWriteFails()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var preview = await fixture.PreviewCsv("NO;KART NO;AD;SOYAD\n30;C30;Bir;Öğrenci\n31;C31;İki;Öğrenci");
        await fixture.Db.Database.ExecuteSqlRawAsync(
            "CREATE TRIGGER fail_second BEFORE INSERT ON students WHEN NEW.student_no = '31' BEGIN SELECT RAISE(ABORT, 'forced'); END;");

        await Assert.ThrowsAsync<DbUpdateException>(() => fixture.Service.ApplyAsync(new(preview.Token), ActorId));

        fixture.Db.ChangeTracker.Clear();
        Assert.False(await fixture.Db.Students.AnyAsync(x => x.StudentNo == "30" || x.StudentNo == "31"));
        Assert.False(await fixture.Db.StudentCards.AnyAsync(x => x.CardNumber == "C30" || x.CardNumber == "C31"));
        Assert.Empty(await fixture.Db.Set<BulkOperation>().ToListAsync());
    }

    [Fact]
    public async Task ErrorsRequireExplicitApplyValidRowsPolicy()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        var preview = await fixture.PreviewCsv("NO;KART NO;AD;SOYAD\n40;C40;Geçerli;Satır\n;C41;Hatalı;Satır");

        await Assert.ThrowsAsync<EntityConflictException>(() => fixture.Service.ApplyAsync(new(preview.Token), ActorId));
        var applied = await fixture.Service.ApplyAsync(new(preview.Token, ApplyValidRows: true), ActorId);

        Assert.Equal(1, applied.CreatedCount);
        Assert.Equal(1, applied.ErrorCount);
        Assert.True(await fixture.Db.Students.AnyAsync(x => x.StudentNo == "40"));
        Assert.Equal(1, await fixture.Db.Students.CountAsync());
    }

    private static MemoryStream CreateXlsx(string[][] rows)
    {
        var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            worksheetPart.Worksheet = new Worksheet(sheetData);
            for (uint rowIndex = 0; rowIndex < rows.Length; rowIndex++)
            {
                var row = new Row { RowIndex = rowIndex + 1 };
                for (var column = 0; column < rows[rowIndex].Length; column++)
                    row.Append(new Cell { CellReference = $"{(char)('A' + column)}{rowIndex + 1}", DataType = CellValues.InlineString, InlineString = new InlineString(new Text(rows[rowIndex][column])) });
                sheetData.Append(row);
            }
            var sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Öğrenciler" });
            workbookPart.Workbook.Save();
        }
        stream.Position = 0;
        return stream;
    }

    private sealed class ImportFixture : IAsyncDisposable
    {
        private readonly SqliteConnection connection;
        public YemekhaneDbContext Db { get; }
        public StudentImportService Service { get; }

        private ImportFixture(SqliteConnection connection, YemekhaneDbContext db)
        {
            this.connection = connection;
            Db = db;
            Service = new StudentImportService(db, new StudentImportPreviewStore(TimeProvider.System), TimeProvider.System);
        }

        public static async Task<ImportFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new ImportFixture(connection, db);
        }

        public Task<ImportPreviewResult> PreviewCsv(string csv) => Service.PreviewAsync(new MemoryStream(Encoding.UTF8.GetBytes(csv)), "students.csv", ActorId);

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
