using Yemekhane.Domain.Entities;

namespace Yemekhane.Application.Organization;

/// <param name="Kind">Sinif turu (<see cref="ClassKinds"/>): Normal ya da Anasinifi.</param>
public sealed record ClassRecord(Guid Id, string Name, bool IsActive, string Kind = ClassKinds.Normal);
public sealed record GroupRecord(Guid Id, string Name, string GroupType, string? CriteriaJson, bool IsActive, int MemberCount);
public sealed record SaveGroupRequest(string Name, string GroupType, string? CriteriaJson = null);

/// <summary>
/// Sinif / sube / bolum / gorev tanimlari tek bir "tanim" sozlesmesiyle yonetilir:
/// eski programdaki (Departman/Bolum/Sinif/Gorev Tanim) dort ayri ekran burada dort
/// tur olarak durur. StudentCount silme karari icindir: kullanilan tanim silinemez.
/// </summary>
public enum LookupKind { Class, Section, Department, Job }

/// <param name="Kind">Yalnizca sinif tanimlarinda dolu: Normal / Anasinifi. Sube, bolum ve gorevde null.</param>
public sealed record LookupRecord(Guid Id, string Name, int StudentCount, string? Kind = null)
{
    /// <summary>Ekran etiketi ("Anasınıfı"); sinif disi tanimlarda bos.</summary>
    public string KindLabel => ClassKinds.Label(Kind);
}

/// <param name="Kind">Sinif icin tur; bos birakilirsa Normal (ekleme) ya da mevcut tur korunur (yeniden adlandirma).</param>
public sealed record SaveLookupRequest(string Name, string? Kind = null);

public interface IOrganizationRepository
{
    Task<IReadOnlyList<ClassRecord>> ListClassesAsync(CancellationToken cancellationToken);
    Task<ClassRecord> AddClassAsync(string name, string kind, CancellationToken cancellationToken);
    Task<GroupRecord> AddGroupAsync(SaveGroupRequest request, CancellationToken cancellationToken);
    Task ReplaceMembersAsync(Guid groupId, IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<LookupRecord>> ListLookupsAsync(LookupKind kind, CancellationToken cancellationToken);
    /// <param name="classKind">Sinif icin tur (dogrulanmis); diger turlerde yok sayilir.</param>
    Task<LookupRecord> AddLookupAsync(LookupKind kind, string name, string? classKind, CancellationToken cancellationToken);
    /// <param name="classKind">Sinif icin yeni tur; null ise mevcut tur korunur.</param>
    Task<LookupRecord> RenameLookupAsync(LookupKind kind, Guid id, string name, string? classKind, CancellationToken cancellationToken);
    Task DeleteLookupAsync(LookupKind kind, Guid id, CancellationToken cancellationToken);
}
