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

    public Task ReplaceMembersAsync(Guid groupId, IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken = default) =>
        repository.ReplaceMembersAsync(groupId, studentIds.Distinct().ToArray(), cancellationToken);

    private static string ValidateName(string name, string field)
    {
        var value = name?.Trim() ?? string.Empty;
        if (value.Length is < 1 or > 100) throw new RequestValidationException($"{field} adı 1-100 karakter olmalıdır.");
        return value;
    }
}
