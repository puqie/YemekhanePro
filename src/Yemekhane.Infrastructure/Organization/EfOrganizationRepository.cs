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

    public async Task<IReadOnlyList<LookupRecord>> ListLookupsAsync(LookupKind kind, CancellationToken cancellationToken)
    {
        var counts = await StudentCountsAsync(kind, cancellationToken);
        var rows = kind switch
        {
            LookupKind.Class => await dbContext.Set<SchoolClass>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken),
            LookupKind.Section => await dbContext.Set<Section>().AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken),
            LookupKind.Department => await dbContext.Set<Department>().AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken),
            LookupKind.Job => await dbContext.Set<Job>().AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Name }).ToListAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return rows.Select(x => new LookupRecord(x.Id, x.Name, counts.GetValueOrDefault(x.Id))).ToList();
    }

    public async Task<LookupRecord> AddLookupAsync(LookupKind kind, string name, CancellationToken cancellationToken)
    {
        if (await NameExistsAsync(kind, name, null, cancellationToken))
            throw new EntityConflictException($"{OrganizationService.LookupLabel(kind)} adı zaten kayıtlı.");
        Entity item = kind switch
        {
            LookupKind.Class => new SchoolClass { Name = name },
            LookupKind.Section => new Section { Name = name },
            LookupKind.Department => new Department { Name = name },
            LookupKind.Job => new Job { Name = name },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        dbContext.Add(item);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new LookupRecord(item.Id, name, 0);
    }

    public async Task<LookupRecord> RenameLookupAsync(LookupKind kind, Guid id, string name, CancellationToken cancellationToken)
    {
        var item = await FindLookupAsync(kind, id, cancellationToken)
            ?? throw new EntityNotFoundException($"{OrganizationService.LookupLabel(kind)} bulunamadı.");
        if (await NameExistsAsync(kind, name, id, cancellationToken))
            throw new EntityConflictException($"{OrganizationService.LookupLabel(kind)} adı zaten kayıtlı.");
        switch (item)
        {
            case SchoolClass c: c.Name = name; break;
            case Section x: x.Name = name; break;
            case Department x: x.Name = name; break;
            case Job x: x.Name = name; break;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        var counts = await StudentCountsAsync(kind, cancellationToken);
        return new LookupRecord(id, name, counts.GetValueOrDefault(id));
    }

    /// <summary>
    /// Kullanilan tanim SILINMEZ: ogrenci FK'si SetNull oldugu icin silme sessizce
    /// ogrencilerin sinifini/subesini bosaltirdi. Once tasima, sonra silme.
    /// </summary>
    public async Task DeleteLookupAsync(LookupKind kind, Guid id, CancellationToken cancellationToken)
    {
        var item = await FindLookupAsync(kind, id, cancellationToken)
            ?? throw new EntityNotFoundException($"{OrganizationService.LookupLabel(kind)} bulunamadı.");
        var used = (await StudentCountsAsync(kind, cancellationToken)).GetValueOrDefault(id);
        if (used > 0)
            throw new EntityConflictException($"{OrganizationService.LookupLabel(kind)} {used} öğrencide kullanılıyor; önce öğrencileri başka bir tanıma taşıyın.");
        dbContext.Remove(item);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<Entity?> FindLookupAsync(LookupKind kind, Guid id, CancellationToken cancellationToken) => kind switch
    {
        LookupKind.Class => await dbContext.Set<SchoolClass>().SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken),
        LookupKind.Section => await dbContext.Set<Section>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken),
        LookupKind.Department => await dbContext.Set<Department>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken),
        LookupKind.Job => await dbContext.Set<Job>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken),
        _ => null
    };

    private async Task<bool> NameExistsAsync(LookupKind kind, string name, Guid? exceptId, CancellationToken cancellationToken) => kind switch
    {
        LookupKind.Class => await dbContext.Set<SchoolClass>().AnyAsync(x => x.Name == name && x.Id != exceptId, cancellationToken),
        LookupKind.Section => await dbContext.Set<Section>().AnyAsync(x => x.Name == name && x.Id != exceptId, cancellationToken),
        LookupKind.Department => await dbContext.Set<Department>().AnyAsync(x => x.Name == name && x.Id != exceptId, cancellationToken),
        LookupKind.Job => await dbContext.Set<Job>().AnyAsync(x => x.Name == name && x.Id != exceptId, cancellationToken),
        _ => false
    };

    /// <summary>Silinmemis ogrencilerin tanim basina sayisi (FK sutununa gore gruplanir).</summary>
    private async Task<Dictionary<Guid, int>> StudentCountsAsync(LookupKind kind, CancellationToken cancellationToken)
    {
        var students = dbContext.Students.AsNoTracking().Where(x => !x.IsDeleted);
        var grouped = kind switch
        {
            LookupKind.Class => students.GroupBy(x => x.ClassId),
            LookupKind.Section => students.GroupBy(x => x.SectionId),
            LookupKind.Department => students.GroupBy(x => x.DepartmentId),
            LookupKind.Job => students.GroupBy(x => x.JobId),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var rows = await grouped.Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        return rows.Where(x => x.Key.HasValue).ToDictionary(x => x.Key!.Value, x => x.Count);
    }

    public async Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken cancellationToken) =>
        await dbContext.Set<StudentGroup>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new GroupRecord(x.Id, x.Name, x.GroupType, x.CriteriaJson, x.IsActive,
                dbContext.Set<StudentGroupMember>().Count(member => member.GroupId == x.Id))).ToListAsync(cancellationToken);
}
