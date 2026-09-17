namespace Yemekhane.Application.Leaves;

public sealed record CreateLeaveRequest(Guid StudentId, DateOnly StartsOn, DateOnly EndsOn, string LeaveType,
    string? Description, string EntitlementBehavior, Guid CreatedBy);
public sealed record LeaveDetails(Guid Id, Guid StudentId, DateOnly StartsOn, DateOnly EndsOn, string LeaveType,
    string? Description, string EntitlementBehavior);

/// <summary>
/// Takvimden birden cok ogrenciye ayni tarih araliginda "ogrenciye ozel tatil" (izin). Her ogrenci
/// icin ayri StudentLeave acilir; hak davranisi hepsine ayni uygulanir.
/// </summary>
public sealed record CreateBulkLeaveRequest(IReadOnlyList<Guid> StudentIds, DateOnly StartsOn, DateOnly EndsOn,
    string LeaveType, string? Description, string EntitlementBehavior);

public sealed record BulkLeaveFailure(Guid StudentId, string Reason);

/// <param name="Created">Acilan izin sayisi.</param>
/// <param name="Failures">Acilamayan ogrenciler ve nedeni (orn. aktarim gunu bulunamadi); digerleri yine acilir.</param>
public sealed record BulkLeaveResult(int Created, IReadOnlyList<BulkLeaveFailure> Failures);

/// <summary>Takvim cekmecesi / listesi icin: tarih araliginda izinli ogrenciler.</summary>
public sealed record LeaveListRow(Guid Id, Guid StudentId, string StudentNo, string StudentName, string? ClassName,
    DateOnly StartsOn, DateOnly EndsOn, string LeaveType, string? Description, string EntitlementBehavior);

public interface ILeaveRepository
{
    Task<LeaveDetails> CreateAndApplyAsync(CreateLeaveRequest request, CancellationToken cancellationToken);
    Task<bool> IsOnLeaveAsync(Guid studentId, DateOnly calendarDate, CancellationToken cancellationToken);
    Task<IReadOnlyList<LeaveDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken);
    /// <summary>Araliga degen (StartsOn &lt;= to ve EndsOn &gt;= from) izinler, ogrenci adiyla.</summary>
    Task<IReadOnlyList<LeaveListRow>> ListInRangeAsync(DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken);
    /// <summary>Izni siler; yalnizca hak davranisi "Keep" olan izin silinebilir (aksi halde false).</summary>
    Task<bool> DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken);
}
