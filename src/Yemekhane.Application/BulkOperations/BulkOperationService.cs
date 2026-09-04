using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;
using Yemekhane.Application.Notifications;

namespace Yemekhane.Application.BulkOperations;

public sealed class BulkPreviewTokenProtector
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);

    public string Protect(string requestHash, string stateHash, DateTimeOffset expiresAt)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new TokenPayload(requestHash, stateHash, expiresAt.ToUnixTimeSeconds()))));
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload)));
        return payload + "." + signature;
    }

    public string Unprotect(string token, string requestHash, DateTimeOffset now)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2) throw Conflict();
        var expected = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(parts[0])));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[1]))) throw Conflict();
        TokenPayload? payload;
        try { payload = JsonSerializer.Deserialize<TokenPayload>(Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]))); }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw Conflict(); }
        if (payload is null || payload.RequestHash != requestHash || payload.ExpiresAt < now.ToUnixTimeSeconds()) throw Conflict();
        return payload.StateHash;
    }

    private static EntityConflictException Conflict() => new("Önizleme süresi doldu veya istek değişti. Yeniden önizleyin.");
    private sealed record TokenPayload(string RequestHash, string StateHash, long ExpiresAt);
}

public sealed class BulkOperationService(IBulkOperationRepository repository, BusinessDayService businessDays,
    BulkPreviewTokenProtector tokens, TimeProvider timeProvider, NotificationService? notifications = null)
{
    private static readonly HashSet<string> Scopes = ["AllSchool", "Class", "Group", "Manual"];
    private static readonly HashSet<string> Operations = ["CancelEntitlements", "Holiday", "Trip", "Leave", "Transfer"];
    private static readonly HashSet<string> Behaviors = ["Delete", "Forfeit", "NextBusinessDay", "SpecifiedDate"];

    public async Task<BulkOperationPreview> PreviewAsync(BulkCalendarOperationRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var state = await repository.PreviewAsync(request, cancellationToken);
        if (state.ScopeStudentIds.Count == 0) throw new RequestValidationException("Kapsamda aktif öğrenci bulunamadı.");
        var targets = await ResolveTargetsAsync(request, state.Entitlements, cancellationToken);
        // Aktarim olmayan islemde hedef tarih YOKTUR (null). GetValueOrDefault DateOnly.MinValue
        // donduruyor, onizleme tablosunda "01.01.0001" gorunuyordu.
        var enriched = state.Entitlements.Select(x => x with { TargetDate = targets.TryGetValue(x.EntitlementId, out var target) ? target : null }).ToArray();
        var warnings = new List<string>();
        var used = state.UsedEntitlementCount;
        if (used > 0) warnings.Add($"{used:N0} hak kaydında kullanılmış hak vardır; yalnızca kalan miktar etkilenir ve işlem geri alınamaz.");
        if (enriched.Length == 0) warnings.Add("Seçilen kapsam ve tarihlerde etkilenebilecek aktif hak bulunamadı.");
        var transfer = IsTransfer(request);
        var quantity = enriched.Sum(x => x.AffectedQuantity);
        var expires = timeProvider.GetUtcNow().AddMinutes(5);
        var requestHash = RequestHash(request, targets);
        return new BulkOperationPreview(state.ScopeStudentIds.Count, enriched.Length, quantity,
            transfer ? 0 : quantity, transfer ? quantity : 0, enriched, targets.Values.Distinct().Order().ToArray(),
            warnings, tokens.Protect(requestHash, state.StateHash, expires), expires);
    }

    public async Task<BulkOperationResult> ApplyAsync(ApplyBulkOperationRequest apply, Guid createdBy, CancellationToken cancellationToken = default)
    {
        Validate(apply.Request);
        var idempotencyHash = RequestHash(apply.Request);
        var replay = await repository.FindIdempotentAsync(apply.Request.IdempotencyKey.Trim(), idempotencyHash, cancellationToken);
        if (replay is not null) return replay with { IdempotentReplay = true };
        var state = await repository.PreviewAsync(apply.Request, cancellationToken);
        var targets = await ResolveTargetsAsync(apply.Request, state.Entitlements, cancellationToken);
        var requestHash = RequestHash(apply.Request, targets);
        var tokenState = tokens.Unprotect(apply.PreviewToken, requestHash, timeProvider.GetUtcNow());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(tokenState), Encoding.ASCII.GetBytes(state.StateHash)))
        {
            replay = await repository.FindIdempotentAsync(apply.Request.IdempotencyKey.Trim(), idempotencyHash, cancellationToken);
            if (replay is not null) return replay with { IdempotentReplay = true };
            throw new EntityConflictException("Önizlemeden sonra kapsam, takvim veya hakediş verisi değişti. Yeniden önizleyin.");
        }
        var result = await repository.ApplyAsync(apply.Request, idempotencyHash, state.StateHash, targets, createdBy, cancellationToken);
        if (notifications is not null)
            await notifications.CreateAsync(new CreateNotification(NotificationSeverities.Success, "BulkOperationCompleted",
                "Toplu işlem tamamlandı", $"{result.StudentCount} öğrenci, {result.Quantity} hak etkilendi.",
                "BulkOperation", result.OperationId.ToString("D"), "entitlements",
                AudiencePermission: "entitlements.bulk", DeduplicationKey: $"bulk:{result.OperationId:D}"), cancellationToken);
        return result;
    }

    public Task<BulkOperationHistoryPage> HistoryAsync(int page, int pageSize, CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 100) throw new RequestValidationException("Sayfalama değerleri geçersiz.");
        return repository.HistoryAsync(page, pageSize, cancellationToken);
    }

    public Task<UndoBulkOperationResult> UndoAsync(Guid operationId, Guid revertedBy, CancellationToken cancellationToken = default) =>
        repository.UndoAsync(operationId, revertedBy, cancellationToken);

    private async Task<Dictionary<Guid, DateOnly>> ResolveTargetsAsync(BulkCalendarOperationRequest request,
        IReadOnlyList<BulkAffectedEntitlement> rows, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, DateOnly>();
        if (!IsTransfer(request)) return result;

        // Belirli bir tarihe aktarim: hepsi AYNI gune gider, yigilma kullanicinin
        // acik istegidir.
        if (request.TransferBehavior == "SpecifiedDate")
        {
            foreach (var row in rows)
            {
                if (request.TargetDate!.Value <= row.Date)
                    throw new RequestValidationException("Aktarım hedefi kaynak tarihten sonra olmalıdır.");
                result[row.EntitlementId] = request.TargetDate.Value;
            }
            return result;
        }

        var scope = new CalendarScope(request.Scope.Type, request.Scope.ScopeId);
        // OGRENCI + OGUN basina ayri planlama: cok gunlu bir tatilde her hak AYRI bir
        // bos is gunune gitmelidir. Once hepsi ayni "sonraki is gunune" tasiniyor ve
        // orada toplaniyordu; bes gunluk tatil, ogrenciye bes ogunluk TEK gun
        // biraktiginda izleyen gunler bos kaliyordu.
        foreach (var group in rows.GroupBy(x => (x.StudentId, x.MealTypeId)))
        {
            var byDate = group.ToDictionary(x => x.Date);
            var plan = await TransferTargetPlanner.PlanAsync(
                byDate.Keys,
                // Kaynak gunler HEDEF OLAMAZ: onlar tatilin kendisidir. Bos
                // sayilsalardi haklar tatilin ICINE kaydirilirdi.
                (date, _) => Task.FromResult(byDate.ContainsKey(date)),
                async (date, token) =>
                {
                    try { return await businessDays.GetNextBusinessDayAsync(date, scope, token); }
                    // On yillik tarama siniri asildi: bu hak icin hedef yok.
                    catch (EntityNotFoundException) { return null; }
                },
                cancellationToken);

            foreach (var (source, target) in plan) result[byDate[source].EntitlementId] = target;

            // Hedef bulunamayan hak SESSIZCE dusurulmez: hak kaybi demektir.
            var placed = plan.Select(x => x.Source).ToHashSet();
            var orphan = byDate.Keys.Where(x => !placed.Contains(x)).ToArray();
            if (orphan.Length > 0)
                throw new RequestValidationException(
                    $"{orphan.Length} hak için uygun bir aktarım günü bulunamadı. Takvimdeki tatilleri gözden geçirin.");
        }
        return result;
    }

    private static bool IsTransfer(BulkCalendarOperationRequest request) =>
        request.TransferBehavior is "NextBusinessDay" or "SpecifiedDate";

    private static void Validate(BulkCalendarOperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Trim().Length > 128)
            throw new RequestValidationException("IdempotencyKey zorunludur ve en fazla 128 karakter olabilir.");
        if (!Scopes.Contains(request.Scope.Type)) throw new RequestValidationException("Kapsam türü geçersiz.");
        if (request.Scope.Type is "Class" or "Group" && !request.Scope.ScopeId.HasValue) throw new RequestValidationException("Kapsam kimliği zorunludur.");
        if (request.Scope.Type == "Manual" && (request.Scope.StudentIds is null || request.Scope.StudentIds.Count == 0)
            && (request.Scope.StudentNos is null || request.Scope.StudentNos.Count == 0)) throw new RequestValidationException("Manuel kapsamda öğrenci seçilmelidir.");
        if (!Operations.Contains(request.Operation)) throw new RequestValidationException("İşlem türü geçersiz.");
        if (!Behaviors.Contains(request.TransferBehavior)) throw new RequestValidationException("Hak davranışı geçersiz.");
        if (request.TransferBehavior == "SpecifiedDate" && !request.TargetDate.HasValue) throw new RequestValidationException("Hedef tarih zorunludur.");
        _ = Dates(request);
    }

    public static IReadOnlyList<DateOnly> Dates(BulkCalendarOperationRequest request)
    {
        var values = (request.Dates ?? []).Distinct().ToList();
        if (request.StartsOn.HasValue || request.EndsOn.HasValue)
        {
            if (!request.StartsOn.HasValue || !request.EndsOn.HasValue || request.EndsOn < request.StartsOn)
                throw new RequestValidationException("Tarih aralığı geçersiz.");
            if (request.EndsOn.Value.DayNumber - request.StartsOn.Value.DayNumber > 366)
                throw new RequestValidationException("Tarih aralığı en fazla 366 gün olabilir.");
            values.AddRange(Enumerable.Range(0, request.EndsOn.Value.DayNumber - request.StartsOn.Value.DayNumber + 1).Select(request.StartsOn.Value.AddDays));
        }
        var result = values.Distinct().Order().ToArray();
        if (result.Length == 0) throw new RequestValidationException("En az bir tarih seçilmelidir.");
        return result;
    }

    public static string RequestHash(BulkCalendarOperationRequest request, IReadOnlyDictionary<Guid, DateOnly>? targets = null)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            Scope = request.Scope.Type, request.Scope.ScopeId,
            Students = (request.Scope.StudentIds ?? []).Distinct().Order(),
            // Numara listesi de istegin parcasidir: ayni idempotency anahtariyla farkli
            // numaralar gonderilirse bu "ayni istek" sayilmamali.
            StudentNos = (request.Scope.StudentNos ?? []).Select(x => x.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            Dates = Dates(request), request.MealTypeId,
            request.Operation, request.TransferBehavior, request.TargetDate, Description = request.Description?.Trim(),
            Targets = targets?.OrderBy(x => x.Key)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
