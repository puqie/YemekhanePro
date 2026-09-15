using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Entitlements;

/// <summary>
/// Atomik tahsilat düzeltmesinden önce "WPF Quick Grant" ile oluşmuş, fakat kasa
/// karşılığı bulunmayan ücretli hakları yükseltmeden sonraki ilk çalışmada telafi eder.
/// </summary>
/// <remarks>
/// İlk çalışma zamanı kalıcı bir kesim noktasıdır. Böylece yükseltmeden SONRA bilerek
/// "Kasaya işle" kapalı verilmiş yeni haklar sonraki API açılışında ücretlendirilmez.
/// Her telafi satırının OperationId değeri hak kimliğinden kararlı üretilir; işlem
/// yarıda kesilip yeniden çalışsa bile aynı gelir ikinci kez yazılamaz.
/// </remarks>
public sealed partial class LegacyEntitlementCashBackfill(
    YemekhaneDbContext dbContext,
    TimeProvider timeProvider,
    IAuditService auditService,
    ILogger<LegacyEntitlementCashBackfill> logger)
{
    public const string CutoffSettingKey = "Repair.LegacyEntitlementCashCutoffUtcV1";
    public const string EligibleSource = "WPF Quick Grant";
    public const string AuditAction = "LegacyEntitlementCashBackfilled";
    private static readonly Guid OperationNamespace = new("87df41cf-ae99-4c5d-a78c-c0e77d288bc3");
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly Regex DateRange = new(
        @"(?<from>\d{2}\.\d{2}\.\d{4})(?:-(?<to>\d{2}\.\d{2}\.\d{4}))?",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DayCount = new(@"\((?<days>\d+)\s*gün\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public async Task<LegacyEntitlementCashBackfillResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var cutoffSetting = await dbContext.Set<SystemSetting>()
            .SingleOrDefaultAsync(x => x.Key == CutoffSettingKey, cancellationToken);
        DateTimeOffset cutoff;
        if (cutoffSetting is null)
        {
            cutoff = now;
            cutoffSetting = new SystemSetting
            {
                Key = CutoffSettingKey,
                Value = cutoff.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                IsSecret = false,
                CreatedAt = now
            };
            dbContext.Add(cutoffSetting);
        }
        else if (!DateTimeOffset.TryParseExact(cutoffSetting.Value, "O", CultureInfo.InvariantCulture,
                     DateTimeStyles.RoundtripKind, out cutoff))
        {
            throw new InvalidOperationException("Eski hakediş kasa telafisi kesim zamanı okunamadı.");
        }

        // SQLite DateTimeOffset sıralama karşılaştırmasını çeviremez. Önce yalnızca
        // ilgili kaynak/durum satırları SQL'de daraltılır; yükseltme kesimi daha sonra
        // bellekte uygulanır. Öğün sayısı düşük olduğu için fiyat/ad sözlükleri ayrıdır.
        var eligibleEntitlements = await dbContext.MealEntitlements.AsNoTracking()
            .Where(x => x.Source == EligibleSource && x.Status == "Active")
            .Select(x => new
            {
                x.Id, x.StudentId, x.MealTypeId, Date = x.EntitlementDate,
                x.Quantity, x.CreatedAt
            })
            .ToListAsync(cancellationToken);
        var mealIds = eligibleEntitlements.Select(x => x.MealTypeId).Distinct().ToArray();
        var prices = await dbContext.Set<MealTypePrice>().AsNoTracking()
            .Where(x => mealIds.Contains(x.MealTypeId) && x.PriceCents > 0)
            .ToDictionaryAsync(x => x.MealTypeId, x => x.PriceCents, cancellationToken);
        var mealNames = await dbContext.Set<MealType>().AsNoTracking()
            .Where(x => mealIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var candidates = eligibleEntitlements
            .Where(x => x.CreatedAt <= cutoff && prices.ContainsKey(x.MealTypeId)
                                              && mealNames.ContainsKey(x.MealTypeId))
            .Select(x => new Candidate
            {
                Id = x.Id,
                StudentId = x.StudentId,
                MealTypeId = x.MealTypeId,
                MealName = mealNames[x.MealTypeId],
                Date = x.Date,
                Quantity = x.Quantity,
                CreatedAt = x.CreatedAt,
                PriceCents = prices[x.MealTypeId]
            }).ToArray();

        var activeCharges = await dbContext.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => !x.IsVoided && x.StudentId != null && x.Description != null
                        && x.Description.StartsWith(EfEntitlementBillingService.DescriptionPrefix))
            .ToListAsync(cancellationToken);
        var existingOperationIds = await dbContext.Set<IncomeTransaction>().AsNoTracking()
            .Select(x => x.OperationId).ToHashSetAsync(cancellationToken);

        var covered = CoveredEntitlements(candidates, activeCharges);
        var missing = candidates
            .Where(x => !covered.Contains(x.Id) && !existingOperationIds.Contains(OperationIdFor(x.Id)))
            .OrderBy(x => x.StudentId).ThenBy(x => x.MealTypeId).ThenBy(x => x.Date)
            .ToArray();

        if (missing.Length == 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LegacyEntitlementCashBackfillResult(0, 0m, cutoff);
        }

        var incomeType = await dbContext.Set<IncomeType>()
            .SingleOrDefaultAsync(x => x.Name == EntitlementIncomeType.Name, cancellationToken);
        if (incomeType is null)
        {
            incomeType = new IncomeType
            {
                Name = EntitlementIncomeType.Name,
                IsActive = true,
                CreatedAt = now
            };
            dbContext.Add(incomeType);
        }
        else if (!incomeType.IsActive)
        {
            incomeType.IsActive = true;
            incomeType.UpdatedAt = now;
        }

        var cards = new Dictionary<Guid, string>();
        foreach (var studentChunk in missing.Select(x => x.StudentId).Distinct().Chunk(500))
        {
            var rows = await dbContext.StudentCards.AsNoTracking()
                .Where(x => studentChunk.Contains(x.StudentId) && x.IsActive)
                .Select(x => new { x.StudentId, x.CardNumber, x.CreatedAt })
                .ToListAsync(cancellationToken);
            foreach (var row in rows.OrderByDescending(x => x.CreatedAt))
                cards.TryAdd(row.StudentId, row.CardNumber);
        }

        decimal total = 0;
        foreach (var item in missing)
        {
            var amount = item.PriceCents * item.Quantity / 100m;
            dbContext.Add(new IncomeTransaction
            {
                OperationId = OperationIdFor(item.Id),
                StudentId = item.StudentId,
                IncomeTypeId = incomeType.Id,
                CardNumber = cards.GetValueOrDefault(item.StudentId),
                TransactionAt = now,
                Amount = amount,
                Description = string.Create(Turkish,
                    $"{EfEntitlementBillingService.DescriptionPrefix} (geçmiş kayıt telafisi): {item.MealName}, {item.Date:dd.MM.yyyy} (1 gün)"),
                CreatedBy = Guid.Empty,
                CreatedAt = now,
                MealTypeId = item.MealTypeId,
                EntitlementStartsOn = item.Date,
                EntitlementEndsOn = item.Date,
                EntitlementDayCount = 1
            });
            total += amount;
        }

        auditService.Record(new AuditEntry(AuditAction, nameof(IncomeTransaction), null,
            string.Create(Turkish,
                $"Geçmiş hızlı hakedişlerin eksik kasa gelirleri bugüne işlendi: {missing.Length:N0} hak, {total:N2} ₺."),
            missing.Length,
            After: new { Cutoff = cutoff, TransactionAt = now, Count = missing.Length, Total = total }));

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        LogCompleted(logger, missing.Length, total, cutoff);
        return new LegacyEntitlementCashBackfillResult(missing.Length, total, cutoff);
    }

    private static HashSet<Guid> CoveredEntitlements(
        IReadOnlyCollection<Candidate> candidates,
        IReadOnlyCollection<IncomeTransaction> charges)
    {
        var covered = new HashSet<Guid>();
        foreach (var group in candidates.GroupBy(x => new { x.StudentId, x.MealTypeId }))
        {
            var items = group.ToArray();
            var matchingCharges = charges
                .Where(x => x.StudentId == group.Key.StudentId
                            && (x.MealTypeId == group.Key.MealTypeId
                                || x.MealTypeId is null && LegacyMealMatches(x.Description, items[0].MealName)))
                .OrderBy(x => x.TransactionAt);

            foreach (var charge in matchingCharges)
            {
                if (!TryRange(charge, out var startsOn, out var endsOn, out var describedDays)) continue;
                var count = charge.EntitlementDayCount is > 0
                    ? charge.EntitlementDayCount.Value
                    : describedDays;
                if (count <= 0) continue;

                // Kısmen örtüşen eski bir işlemde ücretlendirilenler, tahsilata en yakın
                // zamanda YENİ oluşturulmuş haklardır. Böylece aralığın tamamını ücretli
                // sayıp gerçekten eksik günleri atlamayız.
                foreach (var item in items
                             .Where(x => !covered.Contains(x.Id) && x.Date >= startsOn && x.Date <= endsOn)
                             .OrderByDescending(x => x.CreatedAt)
                             .ThenBy(x => x.Date)
                             .Take(count))
                    covered.Add(item.Id);
            }
        }
        return covered;
    }

    private static bool LegacyMealMatches(string? description, string mealName) =>
        description?.Contains($": {mealName},", StringComparison.OrdinalIgnoreCase) == true;

    private static bool TryRange(IncomeTransaction charge, out DateOnly startsOn,
        out DateOnly endsOn, out int dayCount)
    {
        dayCount = 0;
        if (charge.EntitlementStartsOn is { } linkedStart && charge.EntitlementEndsOn is { } linkedEnd)
        {
            startsOn = linkedStart;
            endsOn = linkedEnd;
            dayCount = charge.EntitlementDayCount ?? 0;
            return true;
        }

        var range = DateRange.Match(charge.Description ?? string.Empty);
        if (!range.Success
            || !DateOnly.TryParseExact(range.Groups["from"].Value, "dd.MM.yyyy", Turkish,
                DateTimeStyles.None, out startsOn))
        {
            startsOn = default;
            endsOn = default;
            return false;
        }
        endsOn = startsOn;
        if (range.Groups["to"].Success
            && !DateOnly.TryParseExact(range.Groups["to"].Value, "dd.MM.yyyy", Turkish,
                DateTimeStyles.None, out endsOn))
            return false;
        var count = DayCount.Match(charge.Description ?? string.Empty);
        if (count.Success) _ = int.TryParse(count.Groups["days"].Value,
            NumberStyles.None, CultureInfo.InvariantCulture, out dayCount);
        return true;
    }

    private static Guid OperationIdFor(Guid entitlementId)
    {
        Span<byte> value = stackalloc byte[16];
        Span<byte> seed = stackalloc byte[16];
        entitlementId.TryWriteBytes(value);
        OperationNamespace.TryWriteBytes(seed);
        for (var index = 0; index < value.Length; index++) value[index] ^= seed[index];
        return new Guid(value);
    }

    [LoggerMessage(6401, LogLevel.Information,
        "Geçmiş hakediş kasa telafisi tamamlandı: {Count} hak, {Total} TL, kesim {Cutoff}.")]
    private static partial void LogCompleted(ILogger logger, int count, decimal total,
        DateTimeOffset cutoff);

    private sealed class Candidate
    {
        public Guid Id { get; init; }
        public Guid StudentId { get; init; }
        public Guid MealTypeId { get; init; }
        public required string MealName { get; init; }
        public DateOnly Date { get; init; }
        public int Quantity { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public long PriceCents { get; init; }
    }
}

public sealed record LegacyEntitlementCashBackfillResult(
    int ChargedEntitlements, decimal Total, DateTimeOffset Cutoff);
