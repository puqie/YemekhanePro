namespace Yemekhane.Application.StudentImports;

public sealed record ImportRowError(int RowNumber, string Code, string Message);

// ClassName/Phone: dosyada okunan ama once onizlemede GOSTERILMEYEN alanlar. Operator
// sinif atamasinin ve veli telefonunun dogru okundugunu uygulamadan once goremiyordu.
// Varsayilan degerli eklendi: mevcut cagri yerleri ve JSON sozlesmesi bozulmaz.
public sealed record ImportPreviewRow(
    int RowNumber,
    string StudentNo,
    string CardNumber,
    string FirstName,
    string LastName,
    string Status,
    IReadOnlyList<ImportRowError> Errors,
    string? ClassName = null,
    string? Phone = null);

public sealed record ImportPreviewResult(
    string Token,
    string SnapshotHash,
    DateTimeOffset ExpiresAt,
    int TotalCount,
    int NewCount,
    int UpdateCount,
    int ErrorCount,
    IReadOnlyList<ImportPreviewRow> Rows);

public sealed record ApplyStudentImportRequest(string Token, bool ApplyValidRows = false);

public sealed record ImportApplyResult(Guid OperationId, int CreatedCount, int UpdatedCount, int ErrorCount);

public sealed record ImportErrorReport(byte[] Content, string FileName);

public interface IStudentImportService
{
    Task<ImportPreviewResult> PreviewAsync(Stream content, string fileName, Guid actorId, CancellationToken cancellationToken = default);
    Task<ImportApplyResult> ApplyAsync(ApplyStudentImportRequest request, Guid actorId, CancellationToken cancellationToken = default);
    ImportErrorReport GetErrorReport(string token, Guid actorId);
}
