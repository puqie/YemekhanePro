using Yemekhane.Application.Common;

namespace Yemekhane.Application.Students;

public sealed record StudentQuery(
    string? Search = null,
    string? StudentNo = null,
    string? CardNumber = null,
    string? FirstName = null,
    string? LastName = null,
    Guid? ClassId = null,
    Guid? SectionId = null,
    Guid? DepartmentId = null,
    bool? IsActive = null,
    int Page = 1,
    int PageSize = 50,
    string? ClassName = null,
    string? SectionName = null,
    string? DepartmentName = null,
    Guid? GroupId = null);

public sealed record StudentListItem(
    Guid Id,
    string StudentNo,
    string? CardNumber,
    string FirstName,
    string LastName,
    string? ClassName,
    string? SectionName,
    string? DepartmentName,
    string? ParentPhone,
    bool IsActive,
    int TodayEntitlement,
    bool HasEnteredToday,
    DateTimeOffset? LastEntryAt);

public sealed record StudentDetails(
    Guid Id,
    string StudentNo,
    string? NationalId,
    string FirstName,
    string LastName,
    DateOnly? BirthDate,
    Guid? ClassId,
    Guid? SectionId,
    Guid? DepartmentId,
    Guid? JobId,
    string? FingerprintId,
    string? Pid,
    string? Address,
    string? PhotoPath,
    string? Notes,
    bool IsActive,
    DateOnly RegisteredOn);

public sealed record SaveStudentRequest(
    string StudentNo,
    string FirstName,
    string LastName,
    string? NationalId = null,
    DateOnly? BirthDate = null,
    Guid? ClassId = null,
    Guid? SectionId = null,
    Guid? DepartmentId = null,
    Guid? JobId = null,
    string? FingerprintId = null,
    string? Pid = null,
    string? Address = null,
    string? PhotoPath = null,
    string? Notes = null,
    bool IsActive = true);

public interface IStudentRepository
{
    Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken);
    Task<StudentDetails?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> StudentNoExistsAsync(string studentNo, Guid? excludingId, CancellationToken cancellationToken);
    Task<Guid> AddAsync(SaveStudentRequest request, CancellationToken cancellationToken);
    Task<bool> UpdateAsync(Guid id, SaveStudentRequest request, CancellationToken cancellationToken);
    Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken);
}
