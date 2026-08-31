using Yemekhane.Application.Common;

namespace Yemekhane.Application.Sms;

public sealed class SmsTemplateService(ISmsTemplateRepository repository)
{
    public Task<IReadOnlyList<SmsTemplateDetails>> ListAsync(
        bool includeInactive = false, CancellationToken cancellationToken = default) =>
        repository.ListAsync(includeInactive, cancellationToken);

    public async Task<SmsTemplateDetails> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await repository.GetAsync(id, cancellationToken)
        ?? throw new EntityNotFoundException("SMS şablonu bulunamadı.");

    public async Task<SmsTemplateDetails> CreateAsync(
        SaveSmsTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var valid = Validate(request);
        if (await repository.NameExistsAsync(valid.Name, null, cancellationToken))
            throw new EntityConflictException("SMS şablon adı zaten kayıtlı.");
        return await repository.AddAsync(valid, cancellationToken);
    }

    public async Task<SmsTemplateDetails> UpdateAsync(
        Guid id, SaveSmsTemplateRequest request, CancellationToken cancellationToken = default)
    {
        var valid = Validate(request);
        if (await repository.NameExistsAsync(valid.Name, id, cancellationToken))
            throw new EntityConflictException("SMS şablon adı zaten kayıtlı.");
        return await repository.UpdateAsync(id, valid, cancellationToken)
            ?? throw new EntityNotFoundException("SMS şablonu bulunamadı.");
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await repository.DeactivateAsync(id, cancellationToken))
            throw new EntityNotFoundException("Aktif SMS şablonu bulunamadı.");
    }

    private static SaveSmsTemplateRequest Validate(SaveSmsTemplateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 100)
            throw new RequestValidationException("SMS şablon adı 2-100 karakter olmalıdır.");
        return request with { Name = name, Body = SmsTemplateRenderer.ValidateTemplate(request.Body) };
    }
}
