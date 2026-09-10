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
                CreatedAt = now,
                // Hakedis bagi: iade bu alanlari okur. Description onegine bakip
                // "en son tahsilati" tahmin etmek yanlis ogunun parasini iade ediyordu.
                MealTypeId = request.MealTypeId,
                EntitlementStartsOn = request.StartsOn,
                EntitlementEndsOn = request.EndsOn,
                // Ucretlendirilen gun sayisi: kismi iade bunu kullanir. Ogrenci basina
                // farkli olabilir (kismen ortusen hakedis), o yuzden tutardan turetilir.
                EntitlementDayCount = request.DayCount <= 0 || request.AmountPerStudent <= 0
                    ? request.DayCount
                    : (int)Math.Round(amount / (request.AmountPerStudent / request.DayCount), MidpointRounding.AwayFromZero)
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

    /// <summary>
    /// Iptal edilen haklarin tahsilatini ORANTILI olarak geri alir.
    ///
    /// <para>
    /// Eskiden ogrencinin EN SON hakedis tahsilati bulunup TAMAMI void ediliyordu. Iki
    /// ayri hata uretiyordu: (1) kismi iptalde yenen ogunlerin parasi da iade ediliyor,
    /// (2) eslestirme yalnizca aciklama onegine baktigi icin Ogle iptal edilince
    /// Kahvalti'nin parasi iade edilebiliyordu.
    /// </para>
    /// <para>
    /// Simdi tahsilat OGUN ve TARIH ARALIGI uzerinden eslestirilir; iptal edilen gun
    /// sayisi kadar orantili iade yapilir. Kismi iadede tahsilat void edilir ve KALAN
    /// gunler icin yeni bir tahsilat yazilir: <c>Amount &gt; 0</c> kisiti negatif satira
    /// izin vermiyor, ayrica kasa gecmisinde "su tahsilat iptal edildi, yerine su
    /// yazildi" izi kaliyor.
    /// </para>
    /// </summary>
    public async Task<int> RefundAsync(IReadOnlyCollection<Guid> entitlementIds, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entitlementIds);
        if (entitlementIds.Count == 0) return 0;

        // Iptal edilen haklar: ogrenci, OGUN ve tarih. Ogun bilgisi sart -- ayni ogrencinin
        // farkli ogunlerinin tahsilatlari birbirine karismamali.
        var rows = await dbContext.MealEntitlements.AsNoTracking()
            .Where(x => entitlementIds.Contains(x.Id))
            .Select(x => new { x.StudentId, x.MealTypeId, x.EntitlementDate }).ToListAsync(cancellationToken);
        if (rows.Count == 0) return 0;

        var studentIds = rows.Select(x => x.StudentId).Distinct().ToList();
        var candidates = await dbContext.Set<IncomeTransaction>()
            .Where(x => x.StudentId != null && studentIds.Contains(x.StudentId.Value)
                && !x.IsVoided && x.Description != null && x.Description.StartsWith(DescriptionPrefix))
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0) return 0;

        var now = timeProvider.GetUtcNow();
        var incomeType = await EnsureIncomeTypeAsync(now, cancellationToken);
        var voided = 0;

        foreach (var group in rows.GroupBy(x => new { x.StudentId, x.MealTypeId }))
        {
            var cancelledDates = group.Select(x => x.EntitlementDate).Distinct().ToList();
            // Ayni ogrenci + ayni OGUN icin, iptal edilen gunleri kapsayan tahsilatlar.
            // Eski kayitlarda bag alanlari bos olabilir; o durumda aciklama onegiyle
            // eslesen en son tahsilata dusulur (gecis donemi davranisi).
            var linked = candidates
                .Where(x => x.StudentId == group.Key.StudentId && !x.IsVoided
                    && x.MealTypeId == group.Key.MealTypeId)
                .OrderBy(x => x.TransactionAt).ToList();
            if (linked.Count == 0)
            {
                // GECIS DONEMI: ogun bagi olmayan eski tahsilatlar. Bunlar da ORANTILI
                // iade edilir; once tahsilatin TAMAMI void ediliyordu ve 20 gunluk bir
                // odemede 3 gun iptal edilince yenen 17 ogunun parasi da okuldan cikiyordu.
                var legacy = candidates.Where(x => x.StudentId == group.Key.StudentId && !x.IsVoided
                        && x.MealTypeId == null)
                    .OrderByDescending(x => x.TransactionAt).FirstOrDefault();
                if (legacy is null) continue;
                RefundCharge(legacy, cancelledDates.Count, incomeType, actorId, now);
                voided++;
                continue;
            }

            var remaining = cancelledDates.Count;
            foreach (var charge in linked)
            {
                if (remaining <= 0) break;
                // Bu tahsilatin kapsadigi gunlerden kaci iptal edildi.
                var covered = charge.EntitlementStartsOn is { } from && charge.EntitlementEndsOn is { } to
                    ? cancelledDates.Count(date => date >= from && date <= to)
                    : cancelledDates.Count;
                if (covered <= 0) continue;
                remaining -= RefundCharge(charge, covered, incomeType, actorId, now);
                voided++;
            }
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
    /// <summary>
    /// Iadeyi geri alir: void isareti kaldirilir ve kismi iadede yazilan telafi kaydi
    /// silinir. Telafi kaydi silinmezse para IKI KEZ sayilirdi.
    /// </summary>
    public async Task<int> UndoRefundAsync(IReadOnlyCollection<Guid> entitlementIds, Guid actorId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entitlementIds);
        var ids = entitlementIds.Distinct().ToArray();
        if (ids.Length == 0) return 0;

        var rights = await dbContext.MealEntitlements.AsNoTracking().Where(x => ids.Contains(x.Id))
            .Select(x => new { x.StudentId, x.MealTypeId, x.EntitlementDate })
            .ToListAsync(cancellationToken);
        if (rights.Count == 0) return 0;

        var studentIds = rights.Select(x => x.StudentId).Distinct().ToArray();
        // YALNIZCA hakedis tahsilatlari; baska gelirler bu onekle yazilmaz.
        var voided = await dbContext.Set<IncomeTransaction>()
            .Where(x => studentIds.Contains(x.StudentId!.Value) && x.IsVoided
                && x.Description != null && x.Description.StartsWith(DescriptionPrefix))
            .ToListAsync(cancellationToken);
        if (voided.Count == 0) return 0;

        var now = timeProvider.GetUtcNow();
        var restored = 0;
        foreach (var charge in voided)
        {
            // Kismi iadede yazilan telafi kaydi: ayni turetilmis kimlikle bulunur.
            var compensation = await dbContext.Set<IncomeTransaction>()
                .Where(x => x.OperationId == DeriveOperationId(charge.OperationId, charge.Id))
                .ToListAsync(cancellationToken);
            dbContext.RemoveRange(compensation);

            charge.IsVoided = false;
            charge.VoidedAt = null;
            charge.VoidedBy = null;
            charge.VoidReason = null;
            charge.UpdatedAt = now;
            restored++;
        }

        auditService.Record(new AuditEntry("EntitlementRefundUndone", nameof(IncomeTransaction), null,
            string.Create(Turkish, $"Hakediş iadesi geri alındı: {restored} tahsilat yeniden geçerli."), restored));
        await dbContext.SaveChangesAsync(cancellationToken);
        return restored;
    }

    /// <summary>Tahsilati gecersiz isaretler; tutar silinmez, gerekcesiyle birlikte kalir.</summary>
    private static void Void(IncomeTransaction target, string reason, Guid actorId, DateTimeOffset now)
    {
        target.IsVoided = true;
        target.VoidedAt = now;
        target.VoidedBy = actorId;
        target.VoidReason = reason;
        target.UpdatedAt = now;
    }

    /// <summary>
    /// Bir tahsilati ORANTILI iade eder ve kac gunun iade edildigini doner.
    ///
    /// <para>
    /// Tahsilatin tamami void edilir, sonra KALAN gunlerin bedeli yeni bir kayit
    /// olarak yazilir. Kalan tutar, iade edilecek tutar TAHSILATTAN CIKARILARAK
    /// bulunur -- carpip yuvarlayarak degil.
    /// </para>
    ///
    /// <para>
    /// Cikarma kullanilir, carpip yuvarlama DEGIL: kept + refund toplami her zaman tam
    /// olarak tahsilat tutarini verir. Bugun tahsilat her zaman "birim fiyat x gun"
    /// oldugu icin bolme tam cikar ve iki yontem ayni sonucu uretir; fiyatlandirma
    /// degisip bolunemeyen bir tutar olusursa (indirim, elle duzeltme) cikarma yontemi
    /// kurus kaybetmez, carpip yuvarlama kaybeder.
    /// </para>
    /// </summary>
    private int RefundCharge(IncomeTransaction charge, int coveredDays, IncomeType incomeType,
        Guid actorId, DateTimeOffset now)
    {
        var chargedDays = charge.EntitlementDayCount is > 0 ? charge.EntitlementDayCount.Value : coveredDays;
        var refundDays = Math.Min(coveredDays, chargedDays);
        var keptDays = chargedDays - refundDays;

        Void(charge, keptDays <= 0
            ? "Yemek hakedişi iptal edildi."
            : string.Create(Turkish, $"Yemek hakedişi kısmen iptal edildi: {refundDays}/{chargedDays} gün iade."),
            actorId, now);
        if (keptDays <= 0) return refundDays;

        // Once IADE tutari yuvarlanir, kalan CIKARMA ile bulunur: boylece
        // kept + refund toplami her zaman tam olarak charge.Amount eder.
        var refundAmount = decimal.Round(charge.Amount * refundDays / chargedDays, 2, MidpointRounding.AwayFromZero);
        var keptAmount = charge.Amount - refundAmount;
        if (keptAmount <= 0) return refundDays;

        dbContext.Add(new IncomeTransaction
        {
            // Turetilmis kimlik: ayni iptal iki kez islenirse ikinci kayit acilmaz.
            OperationId = DeriveOperationId(charge.OperationId, charge.Id),
            StudentId = charge.StudentId,
            IncomeTypeId = incomeType.Id,
            CardNumber = charge.CardNumber,
            TransactionAt = now,
            Amount = keptAmount,
            Description = string.Create(Turkish, $"{charge.Description} · kısmi iptal sonrası {keptDays} gün"),
            CreatedBy = actorId,
            CreatedAt = now,
            MealTypeId = charge.MealTypeId,
            EntitlementStartsOn = charge.EntitlementStartsOn,
            EntitlementEndsOn = charge.EntitlementEndsOn,
            EntitlementDayCount = keptDays
        });
        return refundDays;
    }

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
