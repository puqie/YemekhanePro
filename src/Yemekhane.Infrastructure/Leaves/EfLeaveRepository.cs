using System.Globalization;
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
            }

            if (request.EntitlementBehavior == "NextBusinessDay")
            {
                var scope = new CalendarScope("Class", student.ClassId);
                // OGUN TURU BASINA ayri planlanir: ogle ve aksam ayni gunde birlikte
                // bulunabilir, dolayisiyla "gun dolu mu" sorusu ogune baglidir.
                foreach (var group in rights.GroupBy(x => x.MealTypeId))
                {
                    var mealTypeId = group.Key;
                    var byDate = group.ToDictionary(x => x.EntitlementDate);

                    // COK GUNLU TATIL: her hak AYRI bir bos is gunune gider. Once
                    // hepsi ayni "sonraki is gunune" tasiniyordu ve o tek gune
                    // yigiliyordu; bes gunluk tatil bes ogunluk tek gun uretiyordu.
                    var plan = await TransferTargetPlanner.PlanAsync(
                        byDate.Keys,
                        async (date, token) =>
                            // Kaynak gunlerin kendisi hedef sayilmaz: onlar zaten
                            // "Transferred" olarak isaretlendi ve bosaldi.
                            // KAYNAK GUNLER HEDEF OLAMAZ: onlar iznin/tatilin kendisidir
                            // ve satirlari tabloda KALIR (Status=Transferred). Bos
                            // sayilsalardi 7 Eylul'un hakki 8 Eylul'e, 8'inki 9'una
                            // kayar; tatil hic dikkate alinmamis olurdu (olculdu).
                            byDate.ContainsKey(date)
                            // Status'e BAKILMAZ: benzersizlik kisiti
                            // (StudentId, EntitlementDate, MealTypeId) durumdan
                            // bagimsizdir; "Active" filtresi konsaydi iptal edilmis bir
                            // satirin uzerine yazmaya calisip UNIQUE ihlali alirdik.
                            || await dbContext.MealEntitlements.AnyAsync(
                                x => x.StudentId == request.StudentId && x.MealTypeId == mealTypeId
                                    && x.EntitlementDate == date, token),
                        async (date, token) =>
                        {
                            try { return await businessDayService.GetNextBusinessDayAsync(date, scope, token); }
                            // On yillik tarama siniri asildi: bu hak icin hedef yok.
                            catch (EntityNotFoundException) { return null; }
                        },
                        cancellationToken);

                    foreach (var (source, targetDate) in plan)
                    {
                        var right = byDate[source];
                        dbContext.Add(new MealEntitlement
                        {
                            StudentId = right.StudentId, MealTypeId = right.MealTypeId,
                            EntitlementDate = targetDate, Quantity = right.Quantity,
                            Status = "Active", Source = "LeaveTransfer"
                        });
                        dbContext.Add(new MealTransfer
                        {
                            StudentId = right.StudentId, MealTypeId = right.MealTypeId, SourceEntitlementId = right.Id,
                            OriginalDate = right.EntitlementDate, TargetDate = targetDate, Quantity = right.Quantity,
                            Reason = request.Description ?? request.LeaveType, CreatedBy = request.CreatedBy
                        });
                    }

                    // Hedef bulunamayan hak SESSIZCE dusurulmez: hak kaybi demektir.
                    var placed = plan.Select(x => x.Source).ToHashSet();
                    var orphan = byDate.Keys.Where(x => !placed.Contains(x)).ToArray();
                    if (orphan.Length > 0)
                        throw new RequestValidationException(
                            $"{orphan.Length} hak için uygun bir aktarım günü bulunamadı ({string.Join(", ", orphan.Select(x => x.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)))}). "
                            + "Takvimdeki tatilleri gözden geçirin.");
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
