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

    /// <summary>
    /// Aralik icin her gune bir satir: gun bazli kapali-gun sorgusu (IsClosedAsync) ve takvim
    /// projeksiyonu degismeden kalir; satirlar ortak GroupId ile baglanir ki tum aralik tek
    /// hamlede silinebilsin. Tek gunluk tatilde GroupId null'dur.
    /// </summary>
    public async Task<HolidayDetails> CreateAsync(CreateHolidayRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var scopes = request.Scopes.Distinct().ToArray();
        var groupId = request.DayCount > 1 ? Guid.NewGuid() : (Guid?)null;
        Holiday? first = null;
        for (var date = request.Date; date <= request.LastDate; date = date.AddDays(1))
        {
            var holiday = new Holiday
            {
                Date = date, Name = request.Name, HolidayType = request.HolidayType, Description = request.Description,
                TransferBehavior = request.TransferBehavior, GroupId = groupId
            };
            first ??= holiday;
            dbContext.Add(holiday);
            dbContext.AddRange(scopes.Select(scope => new HolidayScope { HolidayId = holiday.Id, ScopeType = scope.ScopeType, ScopeId = scope.ScopeId }));
        }
        auditService.Record(new AuditEntry("HolidayCreated", nameof(Holiday), first!.Id.ToString(),
            request.DayCount == 1 ? "Tatil kaydı oluşturuldu." : $"{request.DayCount} günlük tatil kaydı oluşturuldu.",
            request.DayCount, After: new { request, GroupId = groupId }));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return new HolidayDetails(first.Id, first.Date, first.Name, first.HolidayType, first.Description, first.TransferBehavior, scopes, groupId, request.DayCount);
    }

    public async Task<IReadOnlyList<HolidayDetails>> ListAsync(DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken)
    {
        var holidays = await dbContext.Set<Holiday>().AsNoTracking().Where(x => x.Date >= startsOn && x.Date <= endsOn).OrderBy(x => x.Date).ToListAsync(cancellationToken);
        var ids = holidays.Select(x => x.Id).ToArray();
        var scopes = await dbContext.Set<HolidayScope>().AsNoTracking().Where(x => ids.Contains(x.HolidayId)).ToListAsync(cancellationToken);
        return holidays.Select(x => new HolidayDetails(x.Id, x.Date, x.Name, x.HolidayType, x.Description, x.TransferBehavior,
            scopes.Where(scope => scope.HolidayId == x.Id).Select(scope => new HolidayScopeRequest(scope.ScopeType, scope.ScopeId)).ToArray(), x.GroupId)).ToArray();
    }

    public async Task<int> DeleteAsync(Guid id, bool wholeGroup, CancellationToken cancellationToken)
    {
        var holiday = await dbContext.Set<Holiday>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (holiday is null) return 0;
        var ids = wholeGroup && holiday.GroupId is { } groupId
            ? await dbContext.Set<Holiday>().Where(x => x.GroupId == groupId).Select(x => x.Id).ToListAsync(cancellationToken)
            : [id];
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        // Kapsamlar once: SQLite'ta yabanci anahtar zorlamasi kapaliysa cascade calismaz, yetim kalirlardi.
        await dbContext.Set<HolidayScope>().Where(x => ids.Contains(x.HolidayId)).ExecuteDeleteAsync(cancellationToken);
        var deleted = await dbContext.Set<Holiday>().Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        auditService.Record(new AuditEntry("HolidayDeleted", nameof(Holiday), id.ToString(),
            deleted == 1 ? $"Tatil silindi: {holiday.Name} ({holiday.Date:dd.MM.yyyy})." : $"{deleted} günlük tatil aralığı silindi: {holiday.Name}.",
            deleted, Before: new { holiday.Name, holiday.Date, holiday.GroupId, WholeGroup = wholeGroup }));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return deleted;
    }
}
