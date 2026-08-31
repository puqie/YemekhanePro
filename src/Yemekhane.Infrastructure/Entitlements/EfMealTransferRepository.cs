using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Audit;
using Yemekhane.Infrastructure.Audit;

namespace Yemekhane.Infrastructure.Entitlements;

public sealed class EfMealTransferRepository(YemekhaneDbContext dbContext, IAuditService auditService) : IMealTransferRepository
{
    public async Task<IReadOnlyList<MealTransferDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken) =>
        await dbContext.MealTransfers.AsNoTracking().Where(x => x.StudentId == studentId)
            .OrderByDescending(x => x.OriginalDate)
            .Select(x => new MealTransferDetails(x.Id, x.StudentId, x.MealTypeId, x.OriginalDate, x.TargetDate,
                x.Quantity, x.Reason, x.CreatedBy)).ToListAsync(cancellationToken);

    public EfMealTransferRepository(YemekhaneDbContext dbContext)
        : this(dbContext, new AuditService(new EfAuditRepository(dbContext, TimeProvider.System), new SystemAuditContext())) { }
    public async Task<IReadOnlyList<EntitlementTransferCandidate>> GetCandidatesAsync(IReadOnlyCollection<Guid> entitlementIds, CancellationToken cancellationToken) =>
        await dbContext.MealEntitlements.AsNoTracking()
            .Where(x => entitlementIds.Contains(x.Id) && x.Status == "Active" && x.ConsumedQuantity < x.Quantity)
            .Select(x => new EntitlementTransferCandidate(x.Id, x.StudentId, x.MealTypeId, x.EntitlementDate, x.Quantity - x.ConsumedQuantity))
            .ToListAsync(cancellationToken);

    public async Task<MealTransferResult> TransferAsync(IReadOnlyCollection<EntitlementTransferCommand> commands, string reason,
        Guid createdBy, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var sourceIds = commands.Select(x => x.Source.EntitlementId).ToArray();
        var sources = await dbContext.MealEntitlements.Where(x => sourceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (sources.Count != commands.Count || sources.Values.Any(x => x.Status != "Active" || x.ConsumedQuantity >= x.Quantity))
            throw new EntityConflictException("Yemek hakları aktarım sırasında değişti; işlem uygulanmadı.");

        var targetDates = commands.Select(x => x.TargetDate).Distinct().ToArray();
        var studentIds = commands.Select(x => x.Source.StudentId).Distinct().ToArray();
        var mealIds = commands.Select(x => x.Source.MealTypeId).Distinct().ToArray();
        var targets = await dbContext.MealEntitlements.Where(x => studentIds.Contains(x.StudentId) && mealIds.Contains(x.MealTypeId)
            && targetDates.Contains(x.EntitlementDate)).ToListAsync(cancellationToken);
        var targetMap = targets.ToDictionary(x => (x.StudentId, x.MealTypeId, x.EntitlementDate));
        var total = 0;
        foreach (var command in commands)
        {
            var source = sources[command.Source.EntitlementId];
            var remaining = source.Quantity - source.ConsumedQuantity;
            source.Status = "Transferred"; source.Version++; source.UpdatedAt = DateTimeOffset.UtcNow;
            var key = (source.StudentId, source.MealTypeId, command.TargetDate);
            if (!targetMap.TryGetValue(key, out var target))
            {
                target = new MealEntitlement { StudentId = source.StudentId, MealTypeId = source.MealTypeId,
                    EntitlementDate = command.TargetDate, Quantity = remaining, Status = "Active", Source = "Transfer" };
                dbContext.Add(target); targetMap[key] = target;
            }
            else { target.Quantity += remaining; target.Version++; target.UpdatedAt = DateTimeOffset.UtcNow; }
            dbContext.Add(new MealTransfer { StudentId = source.StudentId, MealTypeId = source.MealTypeId,
                SourceEntitlementId = source.Id, OriginalDate = source.EntitlementDate, TargetDate = command.TargetDate,
                Quantity = remaining, Reason = reason, CreatedBy = createdBy });
            total += remaining;
        }
        var operationId = Guid.NewGuid();
        auditService.Record(new AuditEntry("EntitlementsTransferred", nameof(MealTransfer), operationId.ToString(),
            "Yemek hakları başka tarihe aktarıldı.", commands.Count,
            Before: commands.Select(x => new { x.Source.EntitlementId, x.Source.OriginalDate, x.Source.RemainingQuantity }),
            After: new { TargetDates = targetDates, TotalQuantity = total, reason }, BulkOperationId: operationId, UserId: createdBy));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return new MealTransferResult(commands.Count, total, targetDates);
    }
}
