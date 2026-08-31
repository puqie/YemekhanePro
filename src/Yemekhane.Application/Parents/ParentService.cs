using Yemekhane.Application.Common;

namespace Yemekhane.Application.Parents;

public sealed class ParentService(IParentRepository repository)
{
    public Task<IReadOnlyList<ParentDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        repository.ListAsync(studentId, cancellationToken);

    public Task<ParentDetails> CreateAsync(Guid studentId, SaveParentRequest request, CancellationToken cancellationToken = default) =>
        repository.AddAsync(studentId, Normalize(request), cancellationToken);

    public async Task<ParentDetails> UpdateAsync(Guid parentId, SaveParentRequest request, CancellationToken cancellationToken = default) =>
        await repository.UpdateAsync(parentId, Normalize(request), cancellationToken)
        ?? throw new EntityNotFoundException("Veli kaydı bulunamadı.");

    public async Task DeactivateAsync(Guid parentId, CancellationToken cancellationToken = default)
    {
        if (!await repository.DeactivateAsync(parentId, cancellationToken)) throw new EntityNotFoundException("Aktif veli kaydı bulunamadı.");
    }

    private static SaveParentRequest Normalize(SaveParentRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 200) throw new RequestValidationException("Veli adı 2-200 karakter olmalıdır.");
        var normalizedPhone = TurkishMobilePhone.Normalize(request.Phone);
        return request with { Name = name, Phone = normalizedPhone, Relationship = request.Relationship?.Trim() };
    }
}
