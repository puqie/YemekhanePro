using Yemekhane.Application.Common;

namespace Yemekhane.Application.Sms;

public sealed class SmsService(ISmsLogRepository repository, ISmsTemplateRepository templates)
{
    public async Task<SmsLogDetails> EnqueueAsync(
        EnqueueSmsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var idempotencyKey = request.IdempotencyKey?.Trim() ?? string.Empty;
        if (idempotencyKey.Length is < 1 or > 128)
            throw new RequestValidationException("Idempotency key 1-128 karakter olmalıdır.");

        string message;
        if (request.TemplateId is { } templateId)
        {
            if (!string.IsNullOrWhiteSpace(request.Message))
                throw new RequestValidationException("Mesaj veya şablondan yalnız biri seçilmelidir.");
            var template = await templates.GetAsync(templateId, cancellationToken)
                ?? throw new EntityNotFoundException("Aktif SMS şablonu bulunamadı.");
            if (!template.IsActive) throw new EntityNotFoundException("Aktif SMS şablonu bulunamadı.");
            message = SmsTemplateRenderer.Render(template.Body, request.Variables ??
                new Dictionary<string, object?>());
        }
        else
        {
            message = request.Message?.Trim() ?? string.Empty;
        }
        if (message.Length is < 1 or > 1600)
            throw new RequestValidationException("SMS metni 1-1600 karakter olmalıdır.");

        return await repository.EnqueueAsync(TurkishMobilePhone.Normalize(request.Phone), message,
            idempotencyKey, request.StudentId, request.TemplateId, cancellationToken);
    }

    public Task<PagedResult<SmsLogDetails>> ListAsync(
        SmsHistoryFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Page < 1 || filter.PageSize is < 1 or > 200)
            throw new RequestValidationException("Sayfa en az 1, sayfa boyutu 1-200 olmalıdır.");
        if (filter.From > filter.To)
            throw new RequestValidationException("Başlangıç tarihi bitiş tarihinden sonra olamaz.");
        var phone = string.IsNullOrWhiteSpace(filter.Phone) ? null : TurkishMobilePhone.Normalize(filter.Phone);
        var source = string.IsNullOrWhiteSpace(filter.Source) ? null : filter.Source.Trim();
        if (source is not null && !SmsSources.All.Contains(source, StringComparer.Ordinal))
            throw new RequestValidationException("SMS kaynak filtresi geçersiz.");
        return repository.ListAsync(filter with { Phone = phone, Source = source }, cancellationToken);
    }

    public async Task RetryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!await repository.RetryAsync(id, cancellationToken))
            throw new EntityConflictException("Yalnız başarısız SMS kayıtları yeniden kuyruğa alınabilir.");
    }
}
