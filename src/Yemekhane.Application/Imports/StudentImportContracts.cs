namespace Yemekhane.Application.StudentImports;

public sealed record ImportRowError(int RowNumber, string Code, string Message);

public sealed record ImportPreviewRow(
    int RowNumber,
    string StudentNo,
    string CardNumber,
    string FirstName,
    string LastName,
    string Status,
    IReadOnlyList<ImportRowError> Errors);

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
