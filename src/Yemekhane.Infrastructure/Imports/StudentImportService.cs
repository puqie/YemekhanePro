using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.StudentImports;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Audit;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Application.Notifications;

namespace Yemekhane.Infrastructure.StudentImports;

public sealed class StudentImportPreviewStore(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, StudentImportSnapshot> snapshots = new(StringComparer.Ordinal);
    internal SemaphoreSlim ApplyLock { get; } = new(1, 1);

    internal StudentImportSnapshot Add(StudentImportSnapshot snapshot)
    {
        RemoveExpired();
        snapshots[snapshot.Token] = snapshot;
        return snapshot;
    }

    internal StudentImportSnapshot Get(string token, Guid actorId)
    {
        if (!snapshots.TryGetValue(token, out var snapshot) || snapshot.ActorId != actorId)
            throw new EntityNotFoundException("Import preview bulunamadı.");
        if (snapshot.ExpiresAt <= timeProvider.GetUtcNow())
        {
            snapshots.TryRemove(token, out _);
            throw new EntityConflictException("Import preview süresi dolmuş.");
        }
        if (snapshot.Applied) throw new EntityConflictException("Import preview daha önce uygulanmış.");
        return snapshot;
    }

    internal void MarkApplied(string token)
    {
        if (snapshots.TryGetValue(token, out var snapshot)) snapshot.Applied = true;
    }

    private void RemoveExpired()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in snapshots.Where(x => x.Value.ExpiresAt <= now)) snapshots.TryRemove(item.Key, out _);
    }
}

internal sealed class StudentImportSnapshot
{
    public required string Token { get; init; }
    public required string Hash { get; init; }
    public required string DatabaseHash { get; init; }
    public required Guid ActorId { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required IReadOnlyList<StudentImportRow> Rows { get; init; }
    public bool Applied { get; set; }
}

internal sealed class StudentImportRow
{
    public int RowNumber { get; init; }
    public string StudentNo { get; set; } = string.Empty;
    public string CardNumber { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? NationalId { get; set; }
    public DateOnly? BirthDate { get; set; }
    public string? ClassName { get; set; }
    public Guid? ClassId { get; set; }
    public string? Phone { get; set; }
    public string Status { get; set; } = "New";
    public List<ImportRowError> Errors { get; } = [];
}

public sealed class StudentImportService(
    YemekhaneDbContext dbContext,
    StudentImportPreviewStore store,
    TimeProvider timeProvider,
    IAuditService auditService,
    NotificationService? notifications = null) : IStudentImportService
{
    public StudentImportService(YemekhaneDbContext dbContext, StudentImportPreviewStore store, TimeProvider timeProvider)
        : this(dbContext, store, timeProvider, new AuditService(new EfAuditRepository(dbContext, timeProvider), new SystemAuditContext()), null) { }
    private const int MaxRows = 50_000;
    private const int MaxColumns = 32;
    private const int MaxCellLength = 1_000;
    private const long MaxFileBytes = 10_000_000;
    private const long MaxUncompressedBytes = 50_000_000;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);
    private static readonly string[] RequiredHeaders = ["studentNo", "cardNumber", "firstName", "lastName"];

    private static readonly Dictionary<string, string> HeaderAliases = new(StringComparer.Ordinal)
    {
        ["NO"] = "studentNo", ["OGRENCI NO"] = "studentNo", ["OGRENCI NUMARASI"] = "studentNo", ["OKUL NO"] = "studentNo",
        ["KART NO"] = "cardNumber", ["KART NUMARASI"] = "cardNumber", ["CARD NO"] = "cardNumber",
        ["AD"] = "firstName", ["ADI"] = "firstName", ["FIRST NAME"] = "firstName",
        ["SOYAD"] = "lastName", ["SOYADI"] = "lastName", ["LAST NAME"] = "lastName",
        ["TC"] = "nationalId", ["TC KIMLIK NO"] = "nationalId", ["TCKN"] = "nationalId",
        ["DOGUM TARIHI"] = "birthDate", ["DOGUM TARİHİ"] = "birthDate", ["BIRTH DATE"] = "birthDate",
        ["SINIF"] = "className", ["SINIF ADI"] = "className", ["CLASS"] = "className",
        ["TELEFON"] = "phone", ["TELEFON NO"] = "phone", ["CEP TELEFONU"] = "phone", ["PHONE"] = "phone"
    };

    public async Task<ImportPreviewResult> PreviewAsync(Stream content, string fileName, Guid actorId, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension is not ".csv" and not ".xlsx")
            throw new RequestValidationException("Yalnızca CSV ve XLSX dosyaları desteklenir.");

        await using var buffered = new MemoryStream();
        await CopyLimitedAsync(content, buffered, cancellationToken);
        var fileHash = Convert.ToHexString(SHA256.HashData(buffered.GetBuffer().AsSpan(0, checked((int)buffered.Length))));
        buffered.Position = 0;

        var records = extension == ".csv"
            ? await ReadCsvAsync(buffered, cancellationToken)
            : ReadXlsx(buffered);
        var rows = BuildRows(records);
        await ValidateAgainstDatabaseAsync(rows, cancellationToken);
        var databaseHash = await ComputeDatabaseHashAsync(rows, cancellationToken);
        var normalizedHash = Hash(JsonSerializer.Serialize(rows.Select(x => new
        {
            x.RowNumber, x.StudentNo, x.CardNumber, x.FirstName, x.LastName, x.NationalId,
            x.BirthDate, x.ClassName, x.ClassId, x.Phone, x.Status,
            Errors = x.Errors.Select(e => new { e.Code, e.Message })
        })));
        var snapshotHash = Hash(fileHash + ":" + normalizedHash + ":" + databaseHash);
        var snapshot = store.Add(new StudentImportSnapshot
        {
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
            Hash = snapshotHash,
            DatabaseHash = databaseHash,
            ActorId = actorId,
            ExpiresAt = timeProvider.GetUtcNow().Add(PreviewLifetime),
            Rows = rows
        });
        return ToPreview(snapshot);
    }

    public async Task<ImportApplyResult> ApplyAsync(ApplyStudentImportRequest request, Guid actorId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Token)) throw new RequestValidationException("Preview token zorunludur.");
        await store.ApplyLock.WaitAsync(cancellationToken);
        try
        {
            var snapshot = store.Get(request.Token, actorId);
            var invalidCount = snapshot.Rows.Count(x => x.Errors.Count != 0);
            if (invalidCount != 0 && !request.ApplyValidRows)
                throw new EntityConflictException("Preview hatalı satırlar içeriyor. Yalnız geçerli satırları uygulamak için ApplyValidRows=true gönderin.");
            var currentHash = await ComputeDatabaseHashAsync(snapshot.Rows, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(snapshot.DatabaseHash), Convert.FromHexString(currentHash)))
                throw new EntityConflictException("Preview sonrasında ilgili veriler değişti; yeniden preview oluşturun.");

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            var now = timeProvider.GetUtcNow();
            var operation = new BulkOperation
            {
                IdempotencyKey = $"student-import:{snapshot.Hash}", RequestHash = snapshot.Hash, ResultJson = "{}",
                OperationType = "StudentCardImport", Status = "Completed", CreatedBy = actorId,
                RequestJson = JsonSerializer.Serialize(new { snapshot.Hash, request.ApplyValidRows, Total = snapshot.Rows.Count })
            };
            dbContext.Add(operation);
            var created = 0;
            var updated = 0;
            foreach (var row in snapshot.Rows.Where(x => x.Errors.Count == 0))
            {
                var student = await dbContext.Students.SingleOrDefaultAsync(x => x.StudentNo == row.StudentNo, cancellationToken);
                if (student is null)
                {
                    student = new Student { StudentNo = row.StudentNo, FirstName = row.FirstName, LastName = row.LastName, RegisteredOn = DateOnly.FromDateTime(now.LocalDateTime) };
                    dbContext.Students.Add(student);
                    created++;
                }
                else updated++;

                student.FirstName = row.FirstName;
                student.LastName = row.LastName;
                student.NationalId = row.NationalId;
                student.BirthDate = row.BirthDate;
                student.ClassId = row.ClassId;
                student.IsActive = true;
                student.IsDeleted = false;
                student.UpdatedAt = now;

                var activeCards = await dbContext.StudentCards.Where(x => x.StudentId == student.Id && x.IsActive).ToListAsync(cancellationToken);
                if (activeCards.All(x => x.CardNumber != row.CardNumber))
                {
                    foreach (var oldCard in activeCards)
                    {
                        oldCard.IsActive = false;
                        oldCard.ValidTo = now;
                        oldCard.ReplacementReason = "Sicil importu ile kart değişimi";
                        oldCard.UpdatedAt = now;
                    }
                    dbContext.StudentCards.Add(new StudentCard { StudentId = student.Id, CardNumber = row.CardNumber, ValidFrom = now });
                }

                // TELEFON sutunu okunup dogrulaniyor ama once HICBIR YERE yazilmiyordu: operator
                // dosyaya veli telefonu koyuyor, onizleme kabul ediyor, SMS ekrani ise "telefon yok"
                // diyordu. Ayni numarali aktif veli yoksa "Veli" adiyla kayit acilir; ogrencinin
                // ilk velisi ise birincil olur (SMS alicisi). Var olan veli kayitlarina dokunulmaz.
                if (row.Phone is not null)
                {
                    var parents = await dbContext.Parents.Where(x => x.StudentId == student.Id && x.IsActive).ToListAsync(cancellationToken);
                    if (parents.All(x => x.NormalizedPhone != row.Phone))
                        dbContext.Parents.Add(new Parent
                        {
                            StudentId = student.Id, Name = "Veli", NormalizedPhone = row.Phone,
                            IsPrimary = parents.Count == 0, CreatedAt = now, UpdatedAt = now
                        });
                }
            }

            auditService.Record(new AuditEntry("StudentsImported", nameof(Student), operation.Id.ToString(),
                "Öğrenci ve kart sicili içe aktarıldı.", created + updated,
                After: new { Created = created, Updated = updated, SkippedErrors = invalidCount, snapshot.Hash },
                BulkOperationId: operation.Id, UserId: actorId));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            store.MarkApplied(snapshot.Token);
            if (notifications is not null)
                await notifications.CreateAsync(new CreateNotification(
                    invalidCount == 0 ? NotificationSeverities.Success : NotificationSeverities.Warning,
                    "StudentImportCompleted", "Öğrenci içe aktarma tamamlandı",
                    $"{created} yeni, {updated} güncel, {invalidCount} hatalı satır.", "BulkOperation", operation.Id.ToString("D"), "students",
                    AudiencePermission: "students.write", DeduplicationKey: $"student-import:{operation.Id:D}"), cancellationToken);
            return new ImportApplyResult(operation.Id, created, updated, invalidCount);
        }
        finally { store.ApplyLock.Release(); }
    }

    public ImportErrorReport GetErrorReport(string token, Guid actorId)
    {
        var snapshot = store.Get(token, actorId);
        var csv = new StringBuilder("\uFEFFSatır;NO;KART NO;AD;SOYAD;Hata Kodu;Hata\r\n");
        foreach (var row in snapshot.Rows)
            foreach (var error in row.Errors)
                csv.Append(row.RowNumber).Append(';').Append(Csv(row.StudentNo)).Append(';').Append(Csv(row.CardNumber)).Append(';')
                    .Append(Csv(row.FirstName)).Append(';').Append(Csv(row.LastName)).Append(';').Append(Csv(error.Code)).Append(';')
                    .Append(Csv(error.Message)).Append("\r\n");
        return new ImportErrorReport(Encoding.UTF8.GetBytes(csv.ToString()), $"import-errors-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
    }

    private async Task ValidateAgainstDatabaseAsync(List<StudentImportRow> rows, CancellationToken cancellationToken)
    {
        foreach (var group in rows.Where(x => x.StudentNo.Length != 0).GroupBy(x => x.StudentNo, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            foreach (var row in group) Error(row, "DuplicateStudentNo", "NO dosya içinde birden fazla kez kullanılmış.");
        foreach (var group in rows.Where(x => x.CardNumber.Length != 0).GroupBy(x => x.CardNumber, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            foreach (var row in group) Error(row, "DuplicateCardNumber", "KART NO dosya içinde birden fazla kez kullanılmış.");

        // Global query filter soft-delete edilmis ogrencileri gizler, ancak benzersiz indeks onlari gorur.
        // Filtre uygulanirsa onizleme "Yeni" der, apply ise unique index ihlaliyle TUM aktarimi geri alir.
        var students = await dbContext.Students.IgnoreQueryFilters().AsNoTracking().Where(x => rows.Select(r => r.StudentNo).Contains(x.StudentNo)).ToDictionaryAsync(x => x.StudentNo, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var cards = await dbContext.StudentCards.AsNoTracking().Where(x => rows.Select(r => r.CardNumber).Contains(x.CardNumber)).ToDictionaryAsync(x => x.CardNumber, StringComparer.OrdinalIgnoreCase, cancellationToken);
        var classes = await dbContext.Set<SchoolClass>().AsNoTracking().Where(x => x.IsActive).ToListAsync(cancellationToken);
        var classLookup = classes.GroupBy(x => x.Name.Trim(), StringComparer.Create(new CultureInfo("tr-TR"), true)).ToDictionary(x => x.Key, x => x.Single(), StringComparer.Create(new CultureInfo("tr-TR"), true));

        foreach (var row in rows)
        {
            if (row.ClassName is not null)
            {
                if (classLookup.TryGetValue(row.ClassName, out var schoolClass)) row.ClassId = schoolClass.Id;
                else Error(row, "ClassNotFound", $"'{row.ClassName}' adlı aktif sınıf bulunamadı.");
            }
            students.TryGetValue(row.StudentNo, out var student);
            row.Status = student is null ? "New" : "Update";
            if (cards.TryGetValue(row.CardNumber, out var card) && (student is null || card.StudentId != student.Id || !card.IsActive))
                Error(row, "CardNumberExists", "KART NO veritabanında başka veya geçmiş bir karta aittir.");
            if (row.Errors.Count != 0) row.Status = "Error";
        }
    }

    private async Task<string> ComputeDatabaseHashAsync(IEnumerable<StudentImportRow> rows, CancellationToken cancellationToken)
    {
        var studentNos = rows.Select(x => x.StudentNo).Distinct().Order().ToArray();
        var cardNumbers = rows.Select(x => x.CardNumber).Distinct().Order().ToArray();
        var classNames = rows.Where(x => x.ClassName is not null).Select(x => x.ClassName!).Distinct().Order().ToArray();
        var students = await dbContext.Students.IgnoreQueryFilters().AsNoTracking().Where(x => studentNos.Contains(x.StudentNo))
            .OrderBy(x => x.StudentNo).Select(x => new { x.Id, x.StudentNo, x.FirstName, x.LastName, x.NationalId, x.BirthDate, x.ClassId, x.IsActive, x.IsDeleted, x.UpdatedAt }).ToListAsync(cancellationToken);
        var cards = await dbContext.StudentCards.AsNoTracking().Where(x => cardNumbers.Contains(x.CardNumber) || students.Select(s => s.Id).Contains(x.StudentId))
            .OrderBy(x => x.CardNumber).Select(x => new { x.Id, x.StudentId, x.CardNumber, x.IsActive, x.ValidFrom, x.ValidTo, x.UpdatedAt }).ToListAsync(cancellationToken);
        var classes = await dbContext.Set<SchoolClass>().AsNoTracking().Where(x => classNames.Contains(x.Name)).OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.IsActive, x.UpdatedAt }).ToListAsync(cancellationToken);
        return Hash(JsonSerializer.Serialize(new { students, cards, classes }));
    }

    private static List<StudentImportRow> BuildRows(List<string[]> records)
    {
        if (records.Count == 0) throw new RequestValidationException("Dosya başlık satırı içermiyor.");
        var headers = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < records[0].Length; index++)
        {
            var normalized = NormalizeHeader(records[0][index]);
            if (!HeaderAliases.TryGetValue(normalized, out var canonical)) continue;
            if (!headers.TryAdd(canonical, index)) throw new RequestValidationException($"'{records[0][index]}' başlığı birden fazla kez tanımlanmış.");
        }
        var missing = RequiredHeaders.Where(x => !headers.ContainsKey(x)).ToArray();
        if (missing.Length != 0) throw new RequestValidationException("Zorunlu başlıklar eksik: NO, KART NO, AD, SOYAD.");

        var rows = new List<StudentImportRow>(records.Count - 1);
        for (var index = 1; index < records.Count; index++)
        {
            var cells = records[index];
            if (cells.All(string.IsNullOrWhiteSpace)) continue;
            var row = new StudentImportRow
            {
                RowNumber = index + 1,
                StudentNo = Value("studentNo"), CardNumber = Value("cardNumber"), FirstName = Value("firstName"), LastName = Value("lastName"),
                NationalId = Optional("nationalId"), ClassName = Optional("className"), Phone = Optional("phone")
            };
            ValidateLength(row, row.StudentNo, "NO", 1, 32);
            ValidateLength(row, row.CardNumber, "KART NO", 1, 128);
            ValidateLength(row, row.FirstName, "AD", 1, 100);
            ValidateLength(row, row.LastName, "SOYAD", 1, 100);
            if (row.NationalId is not null && !IsValidTurkishId(row.NationalId)) Error(row, "InvalidNationalId", "TC Kimlik No geçersizdir.");
            var birthText = Optional("birthDate");
            if (birthText is not null)
            {
                if (TryDate(birthText, out var date) && date <= DateOnly.FromDateTime(DateTime.Today)) row.BirthDate = date;
                else Error(row, "InvalidBirthDate", "Doğum tarihi geçersiz veya gelecektedir.");
            }
            if (row.ClassName?.Length > 100) Error(row, "CellTooLong", "SINIF en fazla 100 karakter olabilir.");
            if (row.Phone is not null)
            {
                try { row.Phone = TurkishMobilePhone.Normalize(row.Phone); }
                catch (RequestValidationException exception) { Error(row, "InvalidPhone", exception.Message); }
            }
            rows.Add(row);

            string Value(string name) => headers.TryGetValue(name, out var column) && column < cells.Length ? cells[column].Trim() : string.Empty;
            string? Optional(string name) { var value = Value(name); return value.Length == 0 ? null : value; }
        }
        return rows;
    }

    private static async Task<List<string[]>> ReadCsvAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, true);
        var rawRecords = new List<string>();
        var record = new StringBuilder();
        var quoted = false;
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c == '"')
                    quoted = !quoted;
                if ((c == '\r' || c == '\n') && !quoted)
                {
                    if (c == '\r' && i + 1 < count && buffer[i + 1] == '\n') i++;
                    rawRecords.Add(record.ToString()); record.Clear();
                    if (rawRecords.Count > MaxRows + 1) throw new RequestValidationException($"Dosya en fazla {MaxRows} veri satırı içerebilir.");
                }
                else record.Append(c);
            }
        }
        if (quoted) throw new RequestValidationException("CSV içinde kapanmamış tırnak bulundu.");
        if (record.Length != 0) rawRecords.Add(record.ToString());
        if (rawRecords.Count == 0) return [];
        var delimiter = DetectDelimiter(rawRecords[0]);
        return rawRecords.Select(x => ParseCsvRecord(x, delimiter)).ToList();
    }

    private static List<string[]> ReadXlsx(Stream stream)
    {
        ValidateZip(stream); stream.Position = 0;
        using var document = SpreadsheetDocument.Open(stream, false);
        var workbook = document.WorkbookPart ?? throw new RequestValidationException("XLSX çalışma kitabı geçersiz.");
        var sheet = workbook.Workbook?.Sheets?.Elements<Sheet>().FirstOrDefault() ?? throw new RequestValidationException("XLSX çalışma sayfası bulunamadı.");
        var relationshipId = sheet.Id?.Value ?? throw new RequestValidationException("XLSX çalışma sayfası ilişkisi geçersiz.");
        var worksheet = (WorksheetPart)workbook.GetPartById(relationshipId);
        var shared = workbook.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(x => x.InnerText).ToArray() ?? [];
        var records = new List<string[]>();
        using var reader = OpenXmlReader.Create(worksheet);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
            if (reader.LoadCurrentElement() is not Row row) continue;
            var cells = new List<string>();
            foreach (var cell in row.Elements<Cell>())
            {
                var column = ColumnIndex(cell.CellReference?.Value);
                if (column >= MaxColumns) throw new RequestValidationException($"XLSX en fazla {MaxColumns} sütun içerebilir.");
                while (cells.Count <= column) cells.Add(string.Empty);
                var value = cell.DataType?.Value == CellValues.SharedString && int.TryParse(cell.CellValue?.Text, out var sharedIndex) && sharedIndex < shared.Length
                    ? shared[sharedIndex]
                    : cell.InlineString?.InnerText ?? cell.CellValue?.Text ?? string.Empty;
                if (value.Length > MaxCellLength) throw new RequestValidationException($"{row.RowIndex}. satırdaki hücre {MaxCellLength} karakter sınırını aşıyor.");
                cells[column] = value;
            }
            records.Add(cells.ToArray());
            if (records.Count > MaxRows + 1) throw new RequestValidationException($"Dosya en fazla {MaxRows} veri satırı içerebilir.");
        }
        return records;
    }

    private static void ValidateZip(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, true);
        if (archive.Entries.Count > 2_000) throw new RequestValidationException("XLSX çok fazla ZIP girdisi içeriyor.");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            total = checked(total + entry.Length);
            if (total > MaxUncompressedBytes || entry.Length > MaxUncompressedBytes)
                throw new RequestValidationException("XLSX açılmış boyut sınırını aşıyor.");
            if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > 100)
                throw new RequestValidationException("XLSX güvenli sıkıştırma oranını aşıyor.");
        }
    }

    private static async Task CopyLimitedAsync(Stream source, MemoryStream target, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) break;
            if (target.Length + read > MaxFileBytes) throw new RequestValidationException("Dosya 10.000.000 bayt sınırını aşıyor.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (target.Length == 0) throw new RequestValidationException("Dosya boş.");
    }

    private static char DetectDelimiter(string header)
    {
        var delimiters = new[] { ';', ',', '\t' };
        var selected = delimiters.OrderByDescending(x => CountOutsideQuotes(header, x)).First();
        if (CountOutsideQuotes(header, selected) == 0) throw new RequestValidationException("CSV ayıracı algılanamadı; noktalı virgül, virgül veya sekme kullanın.");
        return selected;
    }

    private static string[] ParseCsvRecord(string record, char delimiter)
    {
        var fields = new List<string>(); var field = new StringBuilder(); var quoted = false;
        for (var i = 0; i < record.Length; i++)
        {
            var c = record[i];
            if (c == '"')
            {
                if (quoted && i + 1 < record.Length && record[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == delimiter && !quoted) { fields.Add(field.ToString()); field.Clear(); }
            else field.Append(c);
        }
        fields.Add(field.ToString());
        if (fields.Count > MaxColumns) throw new RequestValidationException($"CSV en fazla {MaxColumns} sütun içerebilir.");
        if (fields.Any(x => x.Length > MaxCellLength)) throw new RequestValidationException($"CSV hücresi {MaxCellLength} karakter sınırını aşıyor.");
        return fields.ToArray();
    }

    private static int CountOutsideQuotes(string value, char delimiter)
    {
        var count = 0; var quoted = false;
        foreach (var c in value) { if (c == '"') quoted = !quoted; else if (c == delimiter && !quoted) count++; }
        return count;
    }

    private static int ColumnIndex(string? reference)
    {
        var index = 0;
        foreach (var c in reference ?? "A")
        {
            if (!char.IsLetter(c)) break;
            index = index * 26 + char.ToUpperInvariant(c) - 'A' + 1;
        }
        return Math.Max(0, index - 1);
    }

    private static string NormalizeHeader(string value)
    {
        var upper = value.Trim().TrimStart('\uFEFF').ToUpper(new CultureInfo("tr-TR"))
            .Replace('İ', 'I').Replace('Ş', 'S').Replace('Ğ', 'G').Replace('Ü', 'U').Replace('Ö', 'O').Replace('Ç', 'C');
        return string.Join(' ', upper.Split([' ', '_', '-', '.'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsValidTurkishId(string value)
    {
        if (value.Length != 11 || value[0] == '0' || value.Any(c => !char.IsDigit(c))) return false;
        var d = value.Select(c => c - '0').ToArray();
        var tenthDigit = ((d[0] + d[2] + d[4] + d[6] + d[8]) * 7 - (d[1] + d[3] + d[5] + d[7])) % 10;
        if (tenthDigit < 0) tenthDigit += 10;
        return tenthDigit == d[9]
            && d.Take(10).Sum() % 10 == d[10];
    }

    private static bool TryDate(string value, out DateOnly date)
    {
        var formats = new[] { "d.M.yyyy", "dd.MM.yyyy", "d/M/yyyy", "dd/MM/yyyy", "yyyy-MM-dd" };
        if (DateOnly.TryParseExact(value, formats, new CultureInfo("tr-TR"), DateTimeStyles.None, out date)) return true;
        if (double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var serial) && serial is >= 1 and <= 2_958_465)
        {
            date = DateOnly.FromDateTime(DateTime.FromOADate(serial)); return true;
        }
        return false;
    }

    private static void ValidateLength(StudentImportRow row, string value, string field, int min, int max)
    {
        if (value.Length < min) Error(row, "Required", $"{field} zorunludur.");
        else if (value.Length > max) Error(row, "CellTooLong", $"{field} en fazla {max} karakter olabilir.");
    }

    private static void Error(StudentImportRow row, string code, string message) => row.Errors.Add(new(row.RowNumber, code, message));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    /// <summary>
    /// Hata raporu operator tarafindan Excel'de aciliyor. Tirnak kacisi tek basina yetmez:
    /// = + - @ ile baslayan hucreler formul olarak calisir, bu yuzden bir tirnak ile etkisizlestirilir.
    /// </summary>
    private static string Csv(string value)
    {
        var safe = value.Length > 0 && (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            ? "'" + value
            : value;
        return '"' + safe.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static ImportPreviewResult ToPreview(StudentImportSnapshot snapshot) => new(
        snapshot.Token, snapshot.Hash, snapshot.ExpiresAt, snapshot.Rows.Count,
        snapshot.Rows.Count(x => x.Status == "New"), snapshot.Rows.Count(x => x.Status == "Update"), snapshot.Rows.Count(x => x.Status == "Error"),
        snapshot.Rows.Select(x => new ImportPreviewRow(x.RowNumber, x.StudentNo, x.CardNumber, x.FirstName, x.LastName, x.Status, x.Errors.ToArray(), x.ClassName, x.Phone)).ToArray());
}
