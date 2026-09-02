namespace Yemekhane.Application.Organization;

public sealed record ClassRecord(Guid Id, string Name, bool IsActive);
public sealed record GroupRecord(Guid Id, string Name, string GroupType, string? CriteriaJson, bool IsActive, int MemberCount);
public sealed record SaveGroupRequest(string Name, string GroupType, string? CriteriaJson = null);

/// <summary>
/// Sinif / sube / bolum / gorev tanimlari tek bir "tanim" sozlesmesiyle yonetilir:
/// eski programdaki (Departman/Bolum/Sinif/Gorev Tanim) dort ayri ekran burada dort
/// tur olarak durur. StudentCount silme karari icindir: kullanilan tanim silinemez.
/// </summary>
public enum LookupKind { Class, Section, Department, Job }
public sealed record LookupRecord(Guid Id, string Name, int StudentCount);
public sealed record SaveLookupRequest(string Name);

public interface IOrganizationRepository
{
    Task<IReadOnlyList<ClassRecord>> ListClassesAsync(CancellationToken cancellationToken);
    Task<ClassRecord> AddClassAsync(string name, CancellationToken cancellationToken);
    Task<GroupRecord> AddGroupAsync(SaveGroupRequest request, CancellationToken cancellationToken);
    Task ReplaceMembersAsync(Guid groupId, IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<LookupRecord>> ListLookupsAsync(LookupKind kind, CancellationToken cancellationToken);
    Task<LookupRecord> AddLookupAsync(LookupKind kind, string name, CancellationToken cancellationToken);
    Task<LookupRecord> RenameLookupAsync(LookupKind kind, Guid id, string name, CancellationToken cancellationToken);
    Task DeleteLookupAsync(LookupKind kind, Guid id, CancellationToken cancellationToken);
}
