using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Organization;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Organization;

public sealed class EfOrganizationRepository(YemekhaneDbContext dbContext) : IOrganizationRepository
{
    public async Task<IReadOnlyList<ClassRecord>> ListClassesAsync(CancellationToken cancellationToken) =>
        await dbContext.Set<SchoolClass>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new ClassRecord(x.Id, x.Name, x.IsActive)).ToListAsync(cancellationToken);

    public async Task<ClassRecord> AddClassAsync(string name, CancellationToken cancellationToken)
    {
        if (await dbContext.Set<SchoolClass>().AnyAsync(x => x.Name == name, cancellationToken)) throw new EntityConflictException("Sınıf zaten kayıtlı.");
        var item = new SchoolClass { Name = name }; dbContext.Add(item); await dbContext.SaveChangesAsync(cancellationToken);
        return new ClassRecord(item.Id, item.Name, item.IsActive);
    }

    public async Task<GroupRecord> AddGroupAsync(SaveGroupRequest request, CancellationToken cancellationToken)
    {
        if (await dbContext.Set<StudentGroup>().AnyAsync(x => x.Name == request.Name, cancellationToken)) throw new EntityConflictException("Grup zaten kayıtlı.");
        var item = new StudentGroup { Name = request.Name, GroupType = request.GroupType, CriteriaJson = request.CriteriaJson };
        dbContext.Add(item); await dbContext.SaveChangesAsync(cancellationToken); return new GroupRecord(item.Id, item.Name, item.GroupType, item.CriteriaJson, true, 0);
    }

    public async Task ReplaceMembersAsync(Guid groupId, IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var group = await dbContext.Set<StudentGroup>().SingleOrDefaultAsync(x => x.Id == groupId && x.IsActive, cancellationToken)
            ?? throw new EntityNotFoundException("Aktif grup bulunamadı.");
        if (group.GroupType != "Manual") throw new EntityConflictException("Kriter bazlı grubun üyeleri manuel değiştirilemez.");
        var existingStudents = await dbContext.Students.CountAsync(x => studentIds.Contains(x.Id), cancellationToken);
        if (existingStudents != studentIds.Count) throw new EntityNotFoundException("Seçilen öğrencilerden en az biri bulunamadı.");
        await dbContext.Set<StudentGroupMember>().Where(x => x.GroupId == groupId).ExecuteDeleteAsync(cancellationToken);
        dbContext.AddRange(studentIds.Select(studentId => new StudentGroupMember { GroupId = groupId, StudentId = studentId }));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken cancellationToken) =>
        await dbContext.Set<StudentGroup>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new GroupRecord(x.Id, x.Name, x.GroupType, x.CriteriaJson, x.IsActive,
                dbContext.Set<StudentGroupMember>().Count(member => member.GroupId == x.Id))).ToListAsync(cancellationToken);
}
