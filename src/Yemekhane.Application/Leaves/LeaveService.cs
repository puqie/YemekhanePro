using Yemekhane.Application.Common;

namespace Yemekhane.Application.Leaves;

public sealed class LeaveService(ILeaveRepository repository)
{
    private static readonly HashSet<string> Behaviors = ["Keep", "Cancel", "NextBusinessDay"];

    public Task<LeaveDetails> CreateAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request.EndsOn < request.StartsOn) throw new RequestValidationException("İzin bitiş tarihi başlangıç tarihinden önce olamaz.");
        if (string.IsNullOrWhiteSpace(request.LeaveType)) throw new RequestValidationException("İzin türü zorunludur.");
        if (!Behaviors.Contains(request.EntitlementBehavior)) throw new RequestValidationException("İzin yemek hakkı davranışı geçersiz.");
        return repository.CreateAndApplyAsync(request with { LeaveType = request.LeaveType.Trim() }, cancellationToken);
    }

    /// <summary>
    /// Takvimden secilen ogrencilere ayni izin. Bir ogrencide hata (orn. aktarim gunu yok) digerlerini
    /// durdurmaz: acilanlar acilir, acilamayanlar nedeniyle birlikte doner. Ayni ogrenci iki kez
    /// secildiyse tek izin acilir.
    /// </summary>
    public async Task<BulkLeaveResult> CreateManyAsync(CreateBulkLeaveRequest request, Guid actorId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.StudentIds is null || request.StudentIds.Count == 0) throw new RequestValidationException("En az bir öğrenci seçin.");
        if (request.StudentIds.Count > 500) throw new RequestValidationException("Tek seferde en fazla 500 öğrenciye izin verilebilir.");
        if (request.EndsOn < request.StartsOn) throw new RequestValidationException("İzin bitiş tarihi başlangıç tarihinden önce olamaz.");
        if (string.IsNullOrWhiteSpace(request.LeaveType)) throw new RequestValidationException("İzin türü zorunludur.");
        if (!Behaviors.Contains(request.EntitlementBehavior)) throw new RequestValidationException("İzin yemek hakkı davranışı geçersiz.");
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description is { Length: > 500 }) throw new RequestValidationException("Açıklama en fazla 500 karakter olabilir.");

        var created = 0;
        var failures = new List<BulkLeaveFailure>();
        foreach (var studentId in request.StudentIds.Distinct())
        {
            try
            {
                await repository.CreateAndApplyAsync(new CreateLeaveRequest(studentId, request.StartsOn, request.EndsOn,
                    request.LeaveType.Trim(), description, request.EntitlementBehavior, actorId), cancellationToken);
                created++;
            }
            catch (Exception exception) when (exception is RequestValidationException or EntityNotFoundException or EntityConflictException)
            {
                failures.Add(new BulkLeaveFailure(studentId, exception.Message));
            }
        }
        return new BulkLeaveResult(created, failures);
    }

    public Task<IReadOnlyList<LeaveListRow>> ListInRangeAsync(DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken = default)
    {
        if (rangeEnd < rangeStart) throw new RequestValidationException("Bitiş tarihi başlangıçtan önce olamaz.");
        if (rangeEnd.DayNumber - rangeStart.DayNumber > 400) throw new RequestValidationException("Aralık en fazla 400 gün olabilir.");
        return repository.ListInRangeAsync(rangeStart, rangeEnd, cancellationToken);
    }

    public async Task DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken = default)
    {
        if (!await repository.DeleteAsync(id, actorId, cancellationToken))
            throw new EntityNotFoundException("İzin bulunamadı ya da hak etkisi uygulanmış izin silinemez (haklar iptal/aktarım edilmiş).");
    }

    public Task<bool> IsOnLeaveAsync(Guid studentId, DateOnly calendarDate, CancellationToken cancellationToken = default) =>
        repository.IsOnLeaveAsync(studentId, calendarDate, cancellationToken);
    public Task<IReadOnlyList<LeaveDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken = default) => repository.ListAsync(studentId, cancellationToken);
}
