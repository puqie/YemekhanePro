using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Students;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Audit;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.Infrastructure.Students;

public sealed class EfStudentRepository(YemekhaneDbContext dbContext, IAuditService auditService) : IStudentRepository
{
    public EfStudentRepository(YemekhaneDbContext dbContext)
        : this(dbContext, new AuditService(new EfAuditRepository(dbContext, TimeProvider.System), new SystemAuditContext())) { }
    public async Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken)
    {
        var students = dbContext.Students.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.StudentNo)) students = students.Where(x => x.StudentNo == query.StudentNo.Trim());
        if (!string.IsNullOrWhiteSpace(query.FirstName)) students = students.Where(x => EF.Functions.Like(x.FirstName, $"%{query.FirstName.Trim()}%"));
        if (!string.IsNullOrWhiteSpace(query.LastName)) students = students.Where(x => EF.Functions.Like(x.LastName, $"%{query.LastName.Trim()}%"));
        if (query.ClassId.HasValue) students = students.Where(x => x.ClassId == query.ClassId);
        if (query.SectionId.HasValue) students = students.Where(x => x.SectionId == query.SectionId);
        if (query.DepartmentId.HasValue) students = students.Where(x => x.DepartmentId == query.DepartmentId);
        if (query.GroupId.HasValue) students = students.Where(x => dbContext.Set<StudentGroupMember>()
            .Any(member => member.StudentId == x.Id && member.GroupId == query.GroupId));
        if (!string.IsNullOrWhiteSpace(query.ClassName))
        {
            var value = $"%{query.ClassName.Trim()}%";
            students = students.Where(x => dbContext.Set<SchoolClass>().Any(c => c.Id == x.ClassId && EF.Functions.Like(c.Name, value)));
        }
        if (!string.IsNullOrWhiteSpace(query.SectionName))
        {
            var value = $"%{query.SectionName.Trim()}%";
            students = students.Where(x => dbContext.Set<Section>().Any(s => s.Id == x.SectionId && EF.Functions.Like(s.Name, value)));
        }
        if (!string.IsNullOrWhiteSpace(query.DepartmentName))
        {
            var value = $"%{query.DepartmentName.Trim()}%";
            students = students.Where(x => dbContext.Set<Department>().Any(d => d.Id == x.DepartmentId && EF.Functions.Like(d.Name, value)));
        }
        if (query.IsActive.HasValue) students = students.Where(x => x.IsActive == query.IsActive);
        if (!string.IsNullOrWhiteSpace(query.CardNumber))
        {
            var card = query.CardNumber.Trim();
            students = students.Where(student => dbContext.StudentCards.Any(x => x.StudentId == student.Id && x.IsActive && x.CardNumber == card));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = $"%{query.Search.Trim()}%";
            students = students.Where(x => EF.Functions.Like(x.StudentNo, term) || EF.Functions.Like(x.FirstName, term)
                || EF.Functions.Like(x.LastName, term) || dbContext.StudentCards.Any(card => card.StudentId == x.Id
                    && card.IsActive && EF.Functions.Like(card.CardNumber, term)));
        }

        var total = await students.CountAsync(cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var dayStart = new DateTimeOffset(DateTime.Today, TimeZoneInfo.Local.GetUtcOffset(DateTime.Today));
        var dayEnd = dayStart.AddDays(1);
        var items = await students.OrderBy(x => x.StudentNo).Skip((query.Page - 1) * query.PageSize).Take(query.PageSize)
            .Select(student => new StudentListItem(student.Id, student.StudentNo,
                dbContext.StudentCards.Where(card => card.StudentId == student.Id && card.IsActive).Select(card => card.CardNumber).FirstOrDefault(),
                student.FirstName, student.LastName,
                dbContext.Set<SchoolClass>().Where(c => c.Id == student.ClassId).Select(c => c.Name).FirstOrDefault(),
                dbContext.Set<Section>().Where(s => s.Id == student.SectionId).Select(s => s.Name).FirstOrDefault(),
                dbContext.Set<Department>().Where(d => d.Id == student.DepartmentId).Select(d => d.Name).FirstOrDefault(),
                dbContext.Parents.Where(parent => parent.StudentId == student.Id && parent.IsActive)
                    .OrderByDescending(parent => parent.IsPrimary).Select(parent => parent.NormalizedPhone).FirstOrDefault(),
                student.IsActive,
                dbContext.MealEntitlements.Where(x => x.StudentId == student.Id && x.EntitlementDate == today && x.Status == "Active")
                    .Sum(x => (int?)(x.Quantity - x.ConsumedQuantity)) ?? 0,
                dbContext.AccessLogs.Any(x => x.StudentId == student.Id && x.Decision == "ALLOW"
                    && YemekhaneDbContext.JulianDay(x.Timestamp) >= YemekhaneDbContext.JulianDay(dayStart)
                    && YemekhaneDbContext.JulianDay(x.Timestamp) < YemekhaneDbContext.JulianDay(dayEnd)),
                dbContext.AccessLogs.Where(x => x.StudentId == student.Id && x.Decision == "ALLOW")
                    .OrderByDescending(x => YemekhaneDbContext.JulianDay(x.Timestamp))
                    .Select(x => (DateTimeOffset?)x.Timestamp).FirstOrDefault()))
            .ToListAsync(cancellationToken);
        return new PagedResult<StudentListItem>(items, query.Page, query.PageSize, total);
    }

    public Task<StudentDetails?> GetAsync(Guid id, CancellationToken cancellationToken) => dbContext.Students.AsNoTracking()
        .Where(x => x.Id == id).Select(x => new StudentDetails(x.Id, x.StudentNo, x.NationalId, x.FirstName, x.LastName, x.BirthDate,
            x.ClassId, x.SectionId, x.DepartmentId, x.JobId, x.FingerprintId, x.Pid, x.Address, x.PhotoPath, x.Notes, x.IsActive, x.RegisteredOn))
        .SingleOrDefaultAsync(cancellationToken);

    public Task<bool> StudentNoExistsAsync(string studentNo, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.Students.AnyAsync(x => x.StudentNo == studentNo && (!excludingId.HasValue || x.Id != excludingId), cancellationToken);

    public async Task<Guid> AddAsync(SaveStudentRequest request, CancellationToken cancellationToken)
    {
        var student = new Student
        {
            StudentNo = request.StudentNo,
            FirstName = request.FirstName,
            LastName = request.LastName,
            RegisteredOn = DateOnly.FromDateTime(DateTime.Today)
        };
        Apply(student, request);
        dbContext.Students.Add(student);
        LocalOutbox.Enqueue(dbContext, student, LocalOutbox.UpdateStudent, student);
        auditService.Record(new AuditEntry("StudentCreated", nameof(Student), student.Id.ToString(), "Öğrenci oluşturuldu.", After: student));
        await dbContext.SaveChangesAsync(cancellationToken);
        return student.Id;
    }

    public async Task<bool> UpdateAsync(Guid id, SaveStudentRequest request, CancellationToken cancellationToken)
    {
        var student = await dbContext.Students.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (student is null) return false;
        var before = Snapshot(student);
        Apply(student, request); student.UpdatedAt = DateTimeOffset.UtcNow;
        LocalOutbox.Enqueue(dbContext, student, LocalOutbox.UpdateStudent, student);
        auditService.Record(new AuditEntry("StudentUpdated", nameof(Student), id.ToString(), "Öğrenci bilgileri güncellendi.", Before: before, After: student));
        await dbContext.SaveChangesAsync(cancellationToken); return true;
    }

    public async Task<bool> SoftDeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var student = await dbContext.Students.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (student is null) return false;
        var before = Snapshot(student);
        student.IsDeleted = true; student.IsActive = false; student.UpdatedAt = DateTimeOffset.UtcNow;
        LocalOutbox.Enqueue(dbContext, student, LocalOutbox.UpdateStudent, student);
        auditService.Record(new AuditEntry("StudentDeactivated", nameof(Student), id.ToString(), "Öğrenci pasifleştirildi.", Before: before, After: student));
        await dbContext.SaveChangesAsync(cancellationToken); return true;
    }

    private static void Apply(Student student, SaveStudentRequest r)
    {
        student.StudentNo = r.StudentNo; student.FirstName = r.FirstName; student.LastName = r.LastName; student.NationalId = r.NationalId;
        student.BirthDate = r.BirthDate; student.ClassId = r.ClassId; student.SectionId = r.SectionId; student.DepartmentId = r.DepartmentId;
        student.JobId = r.JobId; student.FingerprintId = r.FingerprintId; student.Pid = r.Pid; student.Address = r.Address;
        student.PhotoPath = r.PhotoPath; student.Notes = r.Notes; student.IsActive = r.IsActive;
    }

    private static object Snapshot(Student x) => new
    {
        x.StudentNo, x.NationalId, x.FirstName, x.LastName, x.BirthDate, x.ClassId, x.SectionId,
        x.DepartmentId, x.JobId, x.FingerprintId, x.Pid, x.Address, x.PhotoPath, x.Notes, x.IsActive, x.IsDeleted
    };
}
