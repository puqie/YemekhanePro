using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Parents;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Parents;

public sealed class EfParentRepository(YemekhaneDbContext dbContext) : IParentRepository
{
    public async Task<IReadOnlyList<ParentDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken) =>
        await dbContext.Set<Parent>().AsNoTracking().Where(x => x.StudentId == studentId && x.IsActive)
            .OrderByDescending(x => x.IsPrimary).ThenBy(x => x.Name).Select(ToDetails()).ToListAsync(cancellationToken);

    public async Task<ParentDetails> AddAsync(Guid studentId, SaveParentRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (!await dbContext.Students.AnyAsync(x => x.Id == studentId, cancellationToken)) throw new EntityNotFoundException("Öğrenci bulunamadı.");
        if (request.IsPrimary) await DemotePrimary(studentId, null, cancellationToken);
        var parent = new Parent { StudentId = studentId, Name = request.Name, NormalizedPhone = request.Phone, Relationship = request.Relationship, IsPrimary = request.IsPrimary };
        dbContext.Add(parent); await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return Map(parent);
    }

    public async Task<ParentDetails?> UpdateAsync(Guid parentId, SaveParentRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var parent = await dbContext.Set<Parent>().SingleOrDefaultAsync(x => x.Id == parentId && x.IsActive, cancellationToken);
        if (parent is null) return null;
        if (request.IsPrimary) await DemotePrimary(parent.StudentId, parent.Id, cancellationToken);
        parent.Name = request.Name; parent.NormalizedPhone = request.Phone; parent.Relationship = request.Relationship;
        parent.IsPrimary = request.IsPrimary; parent.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return Map(parent);
    }

    public async Task<bool> DeactivateAsync(Guid parentId, CancellationToken cancellationToken)
    {
        var parent = await dbContext.Set<Parent>().SingleOrDefaultAsync(x => x.Id == parentId && x.IsActive, cancellationToken);
        if (parent is null) return false;
        parent.IsActive = false; parent.IsPrimary = false; parent.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken); return true;
    }

    private Task<int> DemotePrimary(Guid studentId, Guid? exceptId, CancellationToken cancellationToken) =>
        dbContext.Set<Parent>().Where(x => x.StudentId == studentId && x.IsActive && x.IsPrimary && (!exceptId.HasValue || x.Id != exceptId))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsPrimary, false).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);

    private static System.Linq.Expressions.Expression<Func<Parent, ParentDetails>> ToDetails() =>
        x => new ParentDetails(x.Id, x.StudentId, x.Name, x.NormalizedPhone, x.Relationship, x.IsPrimary, x.IsActive);
    private static ParentDetails Map(Parent x) => new(x.Id, x.StudentId, x.Name, x.NormalizedPhone, x.Relationship, x.IsPrimary, x.IsActive);
}
