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
        // Veli adi ISTEGE BAGLI: okulun elinde cogu zaman yalnizca telefon vardir (sicil
        // aktarma da adsiz veli yazar). SMS'in ve kaydin anahtari TELEFONDUR; ad yalnizca
        // gorunumdur ve bos birakilirsa mesajlarda "Veli" diye gecer.
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length > 200) throw new RequestValidationException("Veli adı en fazla 200 karakter olabilir.");
        var normalizedPhone = TurkishMobilePhone.Normalize(request.Phone);
        return request with { Name = name, Phone = normalizedPhone, Relationship = request.Relationship?.Trim() };
    }
}
