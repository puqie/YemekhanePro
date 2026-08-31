using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Calendar;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Audit;
using Yemekhane.Infrastructure.Audit;

namespace Yemekhane.Infrastructure.Calendar;

public sealed class EfHolidayRepository(YemekhaneDbContext dbContext, IAuditService auditService) : IHolidayRepository
{
    public EfHolidayRepository(YemekhaneDbContext dbContext)
        : this(dbContext, new AuditService(new EfAuditRepository(dbContext, TimeProvider.System), new SystemAuditContext())) { }
    public Task<bool> IsClosedAsync(DateOnly calendarDate, CalendarScope scope, CancellationToken cancellationToken) =>
        dbContext.Set<Holiday>().AnyAsync(holiday => holiday.Date == calendarDate &&
            dbContext.Set<HolidayScope>().Any(item => item.HolidayId == holiday.Id &&
                (item.ScopeType == "AllSchool" || (item.ScopeType == scope.ScopeType && item.ScopeId == scope.ScopeId))), cancellationToken);

    public async Task<HolidayDetails> CreateAsync(CreateHolidayRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var holiday = new Holiday { Date = request.Date, Name = request.Name, HolidayType = request.HolidayType,
            Description = request.Description, TransferBehavior = request.TransferBehavior };
        dbContext.Add(holiday);
        dbContext.AddRange(request.Scopes.Distinct().Select(scope => new HolidayScope
        {
            HolidayId = holiday.Id, ScopeType = scope.ScopeType, ScopeId = scope.ScopeId
        }));
        auditService.Record(new AuditEntry("HolidayCreated", nameof(Holiday), holiday.Id.ToString(), "Tatil kaydı oluşturuldu.", After: request));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return new HolidayDetails(holiday.Id, holiday.Date, holiday.Name, holiday.HolidayType, holiday.Description, holiday.TransferBehavior, request.Scopes);
    }

    public async Task<IReadOnlyList<HolidayDetails>> ListAsync(DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken)
    {
        var holidays = await dbContext.Set<Holiday>().AsNoTracking().Where(x => x.Date >= startsOn && x.Date <= endsOn).OrderBy(x => x.Date).ToListAsync(cancellationToken);
        var ids = holidays.Select(x => x.Id).ToArray();
        var scopes = await dbContext.Set<HolidayScope>().AsNoTracking().Where(x => ids.Contains(x.HolidayId)).ToListAsync(cancellationToken);
        return holidays.Select(x => new HolidayDetails(x.Id, x.Date, x.Name, x.HolidayType, x.Description, x.TransferBehavior,
            scopes.Where(scope => scope.HolidayId == x.Id).Select(scope => new HolidayScopeRequest(scope.ScopeType, scope.ScopeId)).ToArray())).ToArray();
    }
}
