using System.Text.Json;
using Yemekhane.Application.Common;

namespace Yemekhane.Application.Organization;

public sealed class OrganizationService(IOrganizationRepository repository)
{
    public Task<IReadOnlyList<ClassRecord>> ListClassesAsync(CancellationToken cancellationToken = default) => repository.ListClassesAsync(cancellationToken);
    public Task<IReadOnlyList<GroupRecord>> ListGroupsAsync(CancellationToken cancellationToken = default) => repository.ListGroupsAsync(cancellationToken);

    public Task<ClassRecord> CreateClassAsync(string name, CancellationToken cancellationToken = default) =>
        repository.AddClassAsync(ValidateName(name, "Sınıf"), cancellationToken);

    public Task<GroupRecord> CreateGroupAsync(SaveGroupRequest request, CancellationToken cancellationToken = default)
    {
        var name = ValidateName(request.Name, "Grup");
        var type = request.GroupType?.Trim();
        if (type is not ("Manual" or "Criteria")) throw new RequestValidationException("Grup tipi Manual veya Criteria olmalıdır.");
        if (type == "Criteria")
        {
            if (string.IsNullOrWhiteSpace(request.CriteriaJson)) throw new RequestValidationException("Kriter grubu için kriter zorunludur.");
            try { JsonDocument.Parse(request.CriteriaJson).Dispose(); }
            catch (JsonException) { throw new RequestValidationException("Grup kriteri geçerli JSON olmalıdır."); }
        }
        return repository.AddGroupAsync(request with { Name = name, GroupType = type }, cancellationToken);
    }

    public Task<IReadOnlyList<LookupRecord>> ListLookupsAsync(LookupKind kind, CancellationToken cancellationToken = default) =>
        repository.ListLookupsAsync(kind, cancellationToken);

    public Task<LookupRecord> CreateLookupAsync(LookupKind kind, string name, CancellationToken cancellationToken = default) =>
        repository.AddLookupAsync(kind, ValidateName(name, LookupLabel(kind)), cancellationToken);

    public Task<LookupRecord> RenameLookupAsync(LookupKind kind, Guid id, string name, CancellationToken cancellationToken = default) =>
        repository.RenameLookupAsync(kind, id, ValidateName(name, LookupLabel(kind)), cancellationToken);

    public Task DeleteLookupAsync(LookupKind kind, Guid id, CancellationToken cancellationToken = default) =>
        repository.DeleteLookupAsync(kind, id, cancellationToken);

    /// <summary>Rota parcasi ("sections") tanim turune cevrilir; bilinmeyen parca 400.</summary>
    public static LookupKind ParseKind(string segment) => segment?.Trim().ToLowerInvariant() switch
    {
        "classes" => LookupKind.Class,
        "sections" => LookupKind.Section,
        "departments" => LookupKind.Department,
        "jobs" => LookupKind.Job,
        _ => throw new RequestValidationException("Tanım türü classes, sections, departments veya jobs olmalıdır.")
    };

    public static string LookupLabel(LookupKind kind) => kind switch
    {
        LookupKind.Class => "Sınıf",
        LookupKind.Section => "Şube",
        LookupKind.Department => "Bölüm",
        LookupKind.Job => "Görev",
        _ => "Tanım"
    };

    public Task ReplaceMembersAsync(Guid groupId, IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken = default) =>
        repository.ReplaceMembersAsync(groupId, studentIds.Distinct().ToArray(), cancellationToken);

    private static string ValidateName(string name, string field)
    {
        var value = name?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 100) throw new RequestValidationException($"{field} adı 1-100 karakter olmalıdır.");
        return value;
    }
}
