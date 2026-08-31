using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yemekhane.Application.Common;

namespace Yemekhane.Application.Sms;

public sealed class SmsPreviewTokenProtector
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    public string Protect(string requestHash, string stateHash, DateTimeOffset expiresAt)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new Payload(requestHash, stateHash, expiresAt.ToUnixTimeSeconds()))));
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
        return payload + "." + signature;
    }

    public string Unprotect(string token, string requestHash, DateTimeOffset now)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2) throw Conflict();
        var expected = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(parts[0])));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1]))) throw Conflict();
        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]))); }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw Conflict(); }
        if (payload is null || payload.RequestHash != requestHash || payload.ExpiresAt < now.ToUnixTimeSeconds()) throw Conflict();
        return payload.StateHash;
    }

    private static EntityConflictException Conflict() => new("SMS önizlemesi geçersiz veya süresi dolmuş. Yeniden önizleyin.");
    private sealed record Payload(string RequestHash, string StateHash, long ExpiresAt);
}

public sealed class BulkSmsService(IBulkSmsRepository repository, ISmsTemplateRepository templates,
    SmsPreviewTokenProtector tokens, TimeProvider timeProvider)
{
    private static readonly HashSet<string> ScopeTypes = ["Manual", "Class", "Group", "All", "Filter"];

    public Task<SmsTargetOptions> TargetsAsync(string? search, CancellationToken cancellationToken = default) =>
        repository.TargetsAsync(string.IsNullOrWhiteSpace(search) ? null : search.Trim(), cancellationToken);

    public async Task<BulkSmsPreview> PreviewAsync(BulkSmsRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var recipients = await BuildRecipientsAsync(request, cancellationToken);
        var expires = timeProvider.GetUtcNow().AddMinutes(5);
        var requestHash = Hash(CanonicalRequest(request));
        var stateHash = Hash(CanonicalRecipients(recipients.Items));
        return new BulkSmsPreview(recipients.Matched, recipients.Items.Count, recipients.NoPhone,
            recipients.Duplicates, recipients.Items.Take(5).ToArray(), tokens.Protect(requestHash, stateHash, expires), expires);
    }

    public async Task<BulkSmsEnqueueResult> ApplyAsync(ApplyBulkSmsRequest apply, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apply);
        Validate(apply.Request);
        var recipients = await BuildRecipientsAsync(apply.Request, cancellationToken);
        var stateHash = tokens.Unprotect(apply.PreviewToken, Hash(CanonicalRequest(apply.Request)), timeProvider.GetUtcNow());
        var currentHash = Hash(CanonicalRecipients(recipients.Items));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stateHash), Encoding.ASCII.GetBytes(currentHash)))
            throw new EntityConflictException("Önizlemeden sonra SMS alıcıları veya mesajları değişti. Yeniden önizleyin.");
        return await repository.EnqueueAsync(recipients.Items, apply.Request.TemplateId,
            apply.Request.IdempotencyKey.Trim(), cancellationToken);
    }

    private async Task<(IReadOnlyList<SmsRecipientPreview> Items, int Matched, int NoPhone, int Duplicates)> BuildRecipientsAsync(
        BulkSmsRequest request, CancellationToken cancellationToken)
    {
        var sources = await repository.ResolveAsync(request.Scope, cancellationToken);
        string body;
        if (request.TemplateId is { } templateId)
        {
            var template = await templates.GetAsync(templateId, cancellationToken);
            if (template is null || !template.IsActive) throw new EntityNotFoundException("Aktif SMS şablonu bulunamadı.");
            body = template.Body;
        }
        else body = request.Message!.Trim();

        var output = new List<SmsRecipientPreview>();
        var phones = new HashSet<string>(StringComparer.Ordinal);
        var noPhone = 0;
        var duplicates = 0;
        foreach (var source in sources.OrderBy(x => x.StudentId))
        {
            string phone;
            try { phone = TurkishMobilePhone.Normalize(source.Phone ?? string.Empty); }
            catch (RequestValidationException) { noPhone++; continue; }
            if (!phones.Add(phone)) { duplicates++; continue; }
            var values = request.Variables is null
                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                : new Dictionary<string, object?>(request.Variables, StringComparer.Ordinal);
            values["StudentName"] = source.StudentName;
            values["ParentName"] = source.ParentName ?? "Veli";
            var message = request.TemplateId.HasValue ? SmsTemplateRenderer.Render(body, values) : body;
            if (message.Length is < 1 or > 1600) throw new RequestValidationException("SMS metni 1-1600 karakter olmalıdır.");
            output.Add(new(source.StudentId, source.StudentName, source.ParentName ?? "Veli", phone, message));
        }
        return (output, sources.Count, noPhone, duplicates);
    }

    private static void Validate(BulkSmsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Trim().Length > 128)
            throw new RequestValidationException("IdempotencyKey zorunludur ve en fazla 128 karakter olabilir.");
        if (request.Scope is null || !ScopeTypes.Contains(request.Scope.Type)) throw new RequestValidationException("SMS kapsam türü geçersiz.");
        if (request.Scope.Type is "Class" or "Group" && !request.Scope.ScopeId.HasValue) throw new RequestValidationException("Kapsam kimliği zorunludur.");
        if (request.Scope.Type == "Manual" && (request.Scope.StudentIds is null || request.Scope.StudentIds.Count == 0))
            throw new RequestValidationException("En az bir öğrenci seçilmelidir.");
        if (request.TemplateId.HasValue == !string.IsNullOrWhiteSpace(request.Message))
            throw new RequestValidationException("Mesaj veya şablondan yalnız biri seçilmelidir.");
        if (!request.TemplateId.HasValue && request.Message!.Trim().Length is < 1 or > 1600)
            throw new RequestValidationException("SMS metni 1-1600 karakter olmalıdır.");
    }

    private static string CanonicalRequest(BulkSmsRequest request) => JsonSerializer.Serialize(request with
    {
        IdempotencyKey = request.IdempotencyKey.Trim(), Message = request.Message?.Trim(),
        Scope = request.Scope with { StudentIds = request.Scope.StudentIds?.Distinct().Order().ToArray() }
    });
    private static string CanonicalRecipients(IReadOnlyList<SmsRecipientPreview> recipients) =>
        JsonSerializer.Serialize(recipients.OrderBy(x => x.StudentId).ThenBy(x => x.Phone));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
