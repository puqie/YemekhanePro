using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Entitlements;

/// <summary>
/// Hakedis ucretlerinin kasa tarafi. Hakedisin kendisi para tasimaz; ucret OGRENCI BASINA
/// ayri bir <see cref="IncomeTransaction"/> olarak yazilir. Boylece tutar kasada ogrenci
/// adiyla gorunur, ogrenci ekstresine duser ve tek tek iptal edilebilir.
///
/// Hakedis kaydiyla ayni transaction'da DEGIL, ondan SONRA calisir: tahsilat ya da SMS
/// hatasi tanimlanmis hakki geri almamalidir (bkz. IncomeService'teki ayni kural).
/// </summary>
public sealed class EfEntitlementBillingService(
    YemekhaneDbContext dbContext,
    TimeProvider timeProvider,
    IAuditService auditService,
    ISmsLogRepository? smsLog = null) : IEntitlementBillingService
{
    /// <summary>Tahsilatin hangi hakedislere ait oldugunu tasiyan aciklama oneki (iade bunu arar).</summary>
    public const string DescriptionPrefix = "Yemek hakedişi";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public async Task<EntitlementChargeResult> ChargeAsync(EntitlementChargeRequest request, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.StudentIds.Count == 0) return new EntitlementChargeResult(0, 0m, 0);
        // Tutari 0 olan ogrenci tahsil edilmez: ayni hakedis ikinci kez verildiginde
        // yeni hak yaratilmadigi icin o ogrencinin borcu yoktur.
        var amountFor = (Guid studentId) => request.AmountOverrides is null
            ? request.AmountPerStudent
            : request.AmountOverrides.GetValueOrDefault(studentId, request.AmountPerStudent);
        if (request.StudentIds.All(x => amountFor(x) <= 0)) return new EntitlementChargeResult(0, 0m, 0);

        // Ayni islem kimligiyle tekrar (masaustu yeniden denemesi): ikinci kez tahsilat
        // yazilmaz. Kimlik ogrenci basina turetildigi icin kontrol de turetilmis kimlikler
        // uzerinden yapilir.
        var operationIds = request.StudentIds.Distinct()
            .ToDictionary(x => x, x => DeriveOperationId(request.OperationId, x));
        var alreadyCharged = await dbContext.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => operationIds.Values.Contains(x.OperationId))
            .Select(x => x.OperationId).ToListAsync(cancellationToken);
        if (alreadyCharged.Count == operationIds.Count) return new EntitlementChargeResult(0, 0m, 0);
        var pending = new HashSet<Guid>(alreadyCharged);

        var now = timeProvider.GetUtcNow();
        var incomeType = await EnsureIncomeTypeAsync(now, cancellationToken);
        var mealName = await dbContext.Set<MealType>().AsNoTracking().Where(x => x.Id == request.MealTypeId)
            .Select(x => x.Name).SingleOrDefaultAsync(cancellationToken) ?? "Öğün";
        var range = request.StartsOn == request.EndsOn
            ? request.StartsOn.ToString("dd.MM.yyyy", Turkish)
            : string.Create(Turkish, $"{request.StartsOn:dd.MM.yyyy}-{request.EndsOn:dd.MM.yyyy}");
        var description = string.Create(Turkish, $"{DescriptionPrefix}: {mealName}, {range} ({request.DayCount} gün)");

        var cards = await dbContext.StudentCards.AsNoTracking()
            .Where(x => request.StudentIds.Contains(x.StudentId) && x.IsActive)
            .GroupBy(x => x.StudentId).Select(g => new { StudentId = g.Key, Card = g.First().CardNumber })
            .ToDictionaryAsync(x => x.StudentId, x => x.Card, cancellationToken);

        var charged = 0; var total = 0m;
        foreach (var (studentId, derived) in operationIds)
        {
            if (pending.Contains(derived)) continue;
            var amount = amountFor(studentId);
            if (amount <= 0) continue;
            dbContext.Add(new IncomeTransaction
            {
                // Ogrenci basina benzersiz islem kimligi: hepsi ayni OperationId tasisaydi
                // tekrar denemede yalnizca ilki yazilirdi.
                OperationId = derived,
                StudentId = studentId,
                IncomeTypeId = incomeType.Id,
                CardNumber = cards.GetValueOrDefault(studentId),
                TransactionAt = now,
                Amount = amount,
                Description = description,
                CreatedBy = actorId,
                CreatedAt = now
            });
            charged++; total += amount;
        }

        auditService.Record(new AuditEntry("EntitlementCharged", nameof(IncomeTransaction), request.OperationId.ToString(),
            string.Create(Turkish, $"Hakediş ücreti kasaya işlendi: {charged} öğrenci, {total:N2} ₺."),
            charged, After: new { request.OperationId, Students = charged, request.AmountPerStudent, Total = total }));
        await dbContext.SaveChangesAsync(cancellationToken);

        var notified = request.NotifyParents
            ? await NotifyParentsAsync(request, mealName, cancellationToken)
            : 0;
        return new EntitlementChargeResult(charged, total, notified);
    }

    public async Task<int> RefundAsync(IReadOnlyCollection<Guid> entitlementIds, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entitlementIds);
        if (entitlementIds.Count == 0) return 0;

        // Iptal edilen haklarin ogrencileri ve tarih araligi; ayni araligin tahsilati geri alinir.
        var rows = await dbContext.MealEntitlements.AsNoTracking()
            .Where(x => entitlementIds.Contains(x.Id))
            .Select(x => new { x.StudentId, x.EntitlementDate }).ToListAsync(cancellationToken);
        if (rows.Count == 0) return 0;

        var studentIds = rows.Select(x => x.StudentId).Distinct().ToList();
        var candidates = await dbContext.Set<IncomeTransaction>()
            .Where(x => x.StudentId != null && studentIds.Contains(x.StudentId.Value)
                && !x.IsVoided && x.Description != null && x.Description.StartsWith(DescriptionPrefix))
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return 0;

        var now = timeProvider.GetUtcNow();
        var voided = 0;
        foreach (var studentId in studentIds)
        {
            // Ogrencinin en son hakedis tahsilati geri alinir; kismi iptalde de tutar
            // kasada asili kalmasin diye tamami iptal edilir ve gerekcesi yazilir.
            var target = candidates.Where(x => x.StudentId == studentId)
                .OrderByDescending(x => x.TransactionAt).FirstOrDefault();
            if (target is null) continue;
            target.IsVoided = true;
            target.VoidedAt = now;
            target.VoidedBy = actorId;
            target.VoidReason = "Yemek hakedişi iptal edildi.";
            target.UpdatedAt = now;
            voided++;
        }
        if (voided == 0) return 0;

        auditService.Record(new AuditEntry("EntitlementChargeRefunded", nameof(IncomeTransaction), null,
            string.Create(Turkish, $"İptal edilen hakedişlerin tahsilatı geri alındı: {voided} kayıt."), voided));
        await dbContext.SaveChangesAsync(cancellationToken);
        return voided;
    }

    /// <summary>Veliye "hakkiniz tanimlandi" bilgisi; SMS ana anahtari kapaliysa kuyrukta bekler.</summary>
    private async Task<int> NotifyParentsAsync(EntitlementChargeRequest request, string mealName,
        CancellationToken cancellationToken)
    {
        if (smsLog is null) return 0;
        var contacts = await dbContext.Students.AsNoTracking()
            .Where(x => request.StudentIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                Name = x.FirstName + " " + x.LastName,
                Phone = dbContext.Set<Parent>().Where(p => p.StudentId == x.Id && p.IsActive)
                    .OrderByDescending(p => p.IsPrimary).Select(p => p.NormalizedPhone).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var queued = 0;
        var failed = 0;
        Exception? lastFailure = null;
        foreach (var contact in contacts.Where(x => !string.IsNullOrWhiteSpace(x.Phone)))
        {
            var message = string.Create(Turkish,
                $"Sayın velimiz, {contact.Name} adlı öğrencimiz için {mealName} hakkı tanımlanmıştır ")
                + string.Create(Turkish, $"({request.StartsOn:dd.MM.yyyy}-{request.EndsOn:dd.MM.yyyy}, {request.DayCount} gün). ")
                + string.Create(Turkish, $"Tutar: {request.AmountPerStudent:N2} ₺.");
            try
            {
                await smsLog.EnqueueAsync(contact.Phone!, message,
                    $"oto:hakedis:{request.OperationId:N}:{contact.Id:N}", contact.Id, null, cancellationToken);
                queued++;
            }
            // SMS kuyruklama hatasi tanimlanmis hakki ya da yazilmis tahsilati geri almamali.
            // Ama SESSIZ de kalmamali: once yutuluyordu ve veli haberdar olmadigi gibi
            // personel de gonderilemedigini ASLA ogrenemiyordu.
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failed++;
                lastFailure = exception;
            }
        }
        if (failed > 0)
        {
            auditService.Record(new AuditEntry("EntitlementSmsQueueFailed", nameof(SmsLog), null,
                string.Create(Turkish, $"Veli SMS'i kuyruğa alınamadı: {failed} kayıt başarısız, {queued} başarılı.")
                + (lastFailure is null ? "" : " Son hata: " + lastFailure.Message), failed));
        }
        return queued;
    }

    private async Task<IncomeType> EnsureIncomeTypeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var type = await dbContext.Set<IncomeType>()
            .SingleOrDefaultAsync(x => x.Name == EntitlementIncomeType.Name, cancellationToken);
        if (type is null)
        {
            type = new IncomeType { Name = EntitlementIncomeType.Name, IsActive = true, CreatedAt = now };
            dbContext.Add(type);
        }
        else if (!type.IsActive)
        {
            type.IsActive = true;
            type.UpdatedAt = now;
        }
        return type;
    }

    /// <summary>Islem + ogrenci ciftinden kararli bir kimlik uretir; tekrar denemede ayni deger cikar.</summary>
    private static Guid DeriveOperationId(Guid operationId, Guid studentId)
    {
        Span<byte> left = stackalloc byte[16];
        Span<byte> right = stackalloc byte[16];
        operationId.TryWriteBytes(left);
        studentId.TryWriteBytes(right);
        for (var index = 0; index < 16; index++) left[index] ^= right[index];
        return new Guid(left);
    }
}
