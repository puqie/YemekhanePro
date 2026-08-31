using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;
using Yemekhane.Application.Leaves;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Audit;
using Yemekhane.Infrastructure.Audit;

namespace Yemekhane.Infrastructure.Leaves;

public sealed class EfLeaveRepository(YemekhaneDbContext dbContext, BusinessDayService businessDayService, IAuditService auditService) : ILeaveRepository
{
    public EfLeaveRepository(YemekhaneDbContext dbContext, BusinessDayService businessDayService)
        : this(dbContext, businessDayService, new AuditService(new EfAuditRepository(dbContext, TimeProvider.System), new SystemAuditContext())) { }
    public async Task<LeaveDetails> CreateAndApplyAsync(CreateLeaveRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var student = await dbContext.Students.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.StudentId, cancellationToken)
            ?? throw new EntityNotFoundException("Öğrenci bulunamadı.");
        var leave = new StudentLeave { StudentId = request.StudentId, StartsOn = request.StartsOn, EndsOn = request.EndsOn,
            LeaveType = request.LeaveType, Description = request.Description, EntitlementBehavior = request.EntitlementBehavior };
        dbContext.Add(leave);
        if (request.EntitlementBehavior != "Keep")
        {
            var rights = await dbContext.MealEntitlements.Where(x => x.StudentId == request.StudentId && x.Status == "Active"
                && x.ConsumedQuantity == 0 && x.EntitlementDate >= request.StartsOn && x.EntitlementDate <= request.EndsOn).ToListAsync(cancellationToken);
            foreach (var right in rights)
            {
                right.Status = request.EntitlementBehavior == "Cancel" ? "Cancelled" : "Transferred"; right.Version++;
                if (request.EntitlementBehavior == "NextBusinessDay")
                {
                    var targetDate = await businessDayService.GetNextBusinessDayAsync(right.EntitlementDate, new CalendarScope("Class", student.ClassId), cancellationToken);
                    var target = await dbContext.MealEntitlements.SingleOrDefaultAsync(x => x.StudentId == right.StudentId && x.MealTypeId == right.MealTypeId && x.EntitlementDate == targetDate, cancellationToken);
                    if (target is null) dbContext.Add(new MealEntitlement { StudentId = right.StudentId, MealTypeId = right.MealTypeId, EntitlementDate = targetDate, Quantity = right.Quantity, Status = "Active", Source = "LeaveTransfer" });
                    else { target.Quantity += right.Quantity; target.Version++; }
                    dbContext.Add(new MealTransfer { StudentId = right.StudentId, MealTypeId = right.MealTypeId, SourceEntitlementId = right.Id,
                        OriginalDate = right.EntitlementDate, TargetDate = targetDate, Quantity = right.Quantity, Reason = request.Description ?? request.LeaveType, CreatedBy = request.CreatedBy });
                }
            }
        }
        auditService.Record(new AuditEntry("LeaveCreated", nameof(StudentLeave), leave.Id.ToString(), "Öğrenci izin kaydı oluşturuldu.", After: request, UserId: request.CreatedBy));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return Map(leave);
    }

    public Task<bool> IsOnLeaveAsync(Guid studentId, DateOnly calendarDate, CancellationToken cancellationToken) =>
        dbContext.Set<StudentLeave>().AnyAsync(x => x.StudentId == studentId && x.StartsOn <= calendarDate && x.EndsOn >= calendarDate, cancellationToken);

    public async Task<IReadOnlyList<LeaveDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken) =>
        await dbContext.Set<StudentLeave>().AsNoTracking().Where(x => x.StudentId == studentId).OrderByDescending(x => x.StartsOn)
            .Select(x => new LeaveDetails(x.Id, x.StudentId, x.StartsOn, x.EndsOn, x.LeaveType, x.Description, x.EntitlementBehavior)).ToListAsync(cancellationToken);

    private static LeaveDetails Map(StudentLeave x) => new(x.Id, x.StudentId, x.StartsOn, x.EndsOn, x.LeaveType, x.Description, x.EntitlementBehavior);
}
