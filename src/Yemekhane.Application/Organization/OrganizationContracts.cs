namespace Yemekhane.Application.Organization;

public sealed record ClassRecord(Guid Id, string Name, bool IsActive);
public sealed record GroupRecord(Guid Id, string Name, string GroupType, string? CriteriaJson, bool IsActive, int MemberCount);
public sealed record SaveGroupRequest(string Name, string GroupType, string? CriteriaJson = null);

public interface IOrganizationRepository
{
    Task<IReadOnlyList<ClassRecord>> ListClassesAsync(CancellationToken cancellationToken);
    Task<ClassRecord> AddClassAsync(string name, CancellationToken cancellationToken);
    Task<GroupRecord> AddGroupAsync(SaveGroupRequest request, CancellationToken cancellationToken);
    Task ReplaceMembersAsync(Guid groupId, IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken cancellationToken);
}
