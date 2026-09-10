using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Common;
using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Tuition;

/// <summary>
/// Ucret planlarinin kaliciligi. Plan kaydedilirken taksitler tek transaction'da yeniden
/// uretilir; ODENMIS taksitler korunur (bkz. <see cref="SaveAsync"/>) ki plan duzeltmesi
/// tahsilat gecmisini silmesin.
/// </summary>
public sealed class EfTuitionRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider, IAuditService auditService)
    : ITuitionRepository
{
    public EfTuitionRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider)
        : this(dbContext, timeProvider, new AuditService(new Audit.EfAuditRepository(dbContext, timeProvider), new Audit.SystemAuditContext())) { }

    public async Task<PagedResult<TuitionPlanDetails>> ListAsync(TuitionPlanFilter filter, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var query = dbContext.TuitionPlans.AsNoTracking();
        if (!filter.IncludeInactive) query = query.Where(x => x.IsActive);
        if (!string.IsNullOrWhiteSpace(filter.Period)) query = query.Where(x => x.Period == filter.Period);
        if (filter.ClassId is { } classId) query = query.Where(x => x.ClassId == classId);
        if (filter.StudentId is { } studentId) query = query.Where(x => x.StudentId == studentId);

        // SQLite DateTimeOffset sutununda ORDER BY ceviremiyor; plan sayisi sinif/ogrenci
        // basina birkac satirdir, siralama ve sayfalama bellekte yapilir.
        var all = await query.ToListAsync(cancellationToken);
        var total = all.Count;
        var plans = all.OrderBy(x => x.Period, StringComparer.Ordinal).ThenBy(x => x.CreatedAt)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize).ToList();
        var items = new List<TuitionPlanDetails>(plans.Count);
        foreach (var plan in plans) items.Add(await DetailsAsync(plan, today, cancellationToken));
        return new PagedResult<TuitionPlanDetails>(items, filter.Page, filter.PageSize, total);
    }

    public async Task<TuitionPlanDetails?> GetAsync(Guid id, DateOnly today, CancellationToken cancellationToken)
    {
        var plan = await dbContext.TuitionPlans.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return plan is null ? null : await DetailsAsync(plan, today, cancellationToken);
    }

    /// <summary>Ogrencinin kendi plani varsa o doner; yoksa sinifinin plani "devralinmis" olarak doner.</summary>
    public async Task<StudentTuitionSummary?> ForStudentAsync(Guid studentId, DateOnly today, CancellationToken cancellationToken)
    {
        var student = await dbContext.Students.AsNoTracking().Where(x => x.Id == studentId)
            .Select(x => new { x.Id, x.StudentNo, Name = x.FirstName + " " + x.LastName, x.ClassId })
            .SingleOrDefaultAsync(cancellationToken);
        if (student is null) return null;

        var className = student.ClassId is null ? null : await dbContext.Set<SchoolClass>().AsNoTracking()
            .Where(x => x.Id == student.ClassId).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken);

        var own = (await dbContext.TuitionPlans.AsNoTracking()
            .Where(x => x.StudentId == studentId && x.IsActive).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        if (own is not null)
            return new StudentTuitionSummary(studentId, student.StudentNo, student.Name, className, false,
                await DetailsAsync(own, today, cancellationToken));

        var classPlan = student.ClassId is null ? null : (await dbContext.TuitionPlans.AsNoTracking()
            .Where(x => x.ClassId == student.ClassId && x.IsActive).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        return new StudentTuitionSummary(studentId, student.StudentNo, student.Name, className, classPlan is not null,
            classPlan is null ? null : await DetailsAsync(classPlan, today, cancellationToken, studentId));
    }

    public async Task<TuitionPlanDetails> SaveAsync(SaveTuitionPlanRequest request, IReadOnlyList<PlannedInstallment> installments,
        DateOnly today, Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(installments);
        var now = timeProvider.GetUtcNow();
        var validated = TuitionService.Validate(request);

        var existing = (request.StudentId is { } studentScope
                ? await dbContext.TuitionPlans
                    .Where(x => x.StudentId == studentScope && x.Period == validated.Period && x.IsActive).ToListAsync(cancellationToken)
                : await dbContext.TuitionPlans
                    .Where(x => x.ClassId == request.ClassId && x.Period == validated.Period && x.IsActive).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault();

        TuitionPlan plan;
        if (existing is null)
        {
            plan = validated;
            plan.CreatedAt = now;
            dbContext.Add(plan);
        }
        else
        {
            plan = existing;
            plan.Kind = validated.Kind;
            plan.AmountCents = validated.AmountCents;
            plan.DownPaymentCents = validated.DownPaymentCents;
            plan.InstallmentCount = validated.InstallmentCount;
            plan.DueDayOfMonth = validated.DueDayOfMonth;
            plan.StartsOn = validated.StartsOn;
            plan.Note = validated.Note;
            plan.IsActive = validated.IsActive;
            plan.UpdatedAt = now;
        }

        var targets = await TargetStudentsAsync(plan, cancellationToken);
        await RebuildInstallmentsAsync(plan, targets, installments, now, cancellationToken);

        auditService.Record(new AuditEntry("TuitionPlanSaved", nameof(TuitionPlan), plan.Id.ToString(),
            $"Ücret planı kaydedildi: {TuitionPlanKinds.Label(plan.Kind)}, {StudentBalanceService.ToLira(plan.AmountCents):N2} ₺, {targets.Count} öğrenci.",
            After: new { plan.Kind, plan.Period, plan.AmountCents, plan.InstallmentCount, Students = targets.Count }));
        await dbContext.SaveChangesAsync(cancellationToken);
        return await DetailsAsync(plan, today, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, Guid actorId, CancellationToken cancellationToken)
    {
        var plan = await dbContext.TuitionPlans.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (plan is null) return false;

        // Odemesi olan plan silinmez, pasife alinir: tahsilat gecmisi kasada durur ve
        // hangi borca sayildigi izlenebilir kalir.
        var planInstallmentIds = await dbContext.TuitionInstallments.Where(x => x.PlanId == id)
            .Select(x => x.Id).ToListAsync(cancellationToken);
        var hasPayments = planInstallmentIds.Count > 0
            && await dbContext.TuitionPayments.AnyAsync(x => planInstallmentIds.Contains(x.InstallmentId), cancellationToken);
        if (hasPayments)
        {
            plan.IsActive = false;
            plan.UpdatedAt = timeProvider.GetUtcNow();
            auditService.Record(new AuditEntry("TuitionPlanDeactivated", nameof(TuitionPlan), id.ToString(),
                "Tahsilatı olan ücret planı silinmedi, pasife alındı."));
        }
        else
        {
            dbContext.RemoveRange(await dbContext.TuitionInstallments.Where(x => x.PlanId == id).ToListAsync(cancellationToken));
            dbContext.Remove(plan);
            auditService.Record(new AuditEntry("TuitionPlanDeleted", nameof(TuitionPlan), id.ToString(), "Ücret planı silindi."));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<TuitionInstallmentDetails> ApplyPaymentAsync(ApplyTuitionPaymentRequest request, DateOnly today,
        Guid actorId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var installment = await dbContext.TuitionInstallments.SingleOrDefaultAsync(x => x.Id == request.InstallmentId, cancellationToken)
            ?? throw new EntityNotFoundException("Taksit bulunamadı.");
        if (installment.IsCancelled) throw new RequestValidationException("İptal edilmiş taksite ödeme işlenemez.");
        if (!await dbContext.Set<IncomeTransaction>().AnyAsync(x => x.Id == request.IncomeTransactionId, cancellationToken))
            throw new EntityNotFoundException("Tahsilat kaydı bulunamadı.");
        // Ayni tahsilat ayni taksite iki kez sayilirsa borc yanlis kapanir.
        if (await dbContext.TuitionPayments.AnyAsync(
                x => x.InstallmentId == request.InstallmentId && x.IncomeTransactionId == request.IncomeTransactionId, cancellationToken))
            throw new EntityConflictException("Bu tahsilat bu taksite zaten işlenmiş.");

        var now = timeProvider.GetUtcNow();
        var cents = StudentBalanceService.ToCents(request.Amount);
        dbContext.Add(new TuitionPayment
        {
            InstallmentId = installment.Id,
            IncomeTransactionId = request.IncomeTransactionId,
            AmountCents = cents,
            PaidAt = now,
            CreatedBy = actorId,
            CreatedAt = now
        });
        auditService.Record(new AuditEntry("TuitionPaymentApplied", nameof(TuitionInstallment), installment.Id.ToString(),
            $"Taksite ödeme işlendi: {request.Amount:N2} ₺.",
            After: new { Added = cents, installment.AmountCents }));
        await dbContext.SaveChangesAsync(cancellationToken);

        // ODENEN TUTAR VERITABANINDA ARTIRILIR, okunan degerin uzerine yazilmaz.
        //
        // Once "installment.PaidCents += cents" yaziliyordu: iki kasiyer AYNI taksite
        // ayri tahsilat islerse ikisi de PaidCents=0 okur, ikincisi birincinin tutarini
        // EZER ve o para taksitte hic gorunmez. Benzersiz indeks bunu yakalamaz (farkli
        // IncomeTransactionId), Version alani da yok. TuitionPayment satirlari dogru
        // kalir ama borc yanlis kapanir -- veliden alinan para kaybolmus gorunur.
        await dbContext.TuitionInstallments.Where(x => x.Id == installment.Id)
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.PaidCents, x => x.PaidCents + cents)
                .SetProperty(x => x.UpdatedAt, now), cancellationToken);

        // Guncel degeri geri oku: bellekteki nesne artik eskidir.
        dbContext.Entry(installment).State = EntityState.Detached;
        var fresh = await dbContext.TuitionInstallments.AsNoTracking()
            .SingleAsync(x => x.Id == installment.Id, cancellationToken);
        return Map(fresh, today);
    }

    /// <summary>Sinif plani o siniftaki AKTIF ogrencilere uygulanir; ogrenci plani yalnizca o ogrenciye.</summary>
    private async Task<IReadOnlyList<Guid>> TargetStudentsAsync(TuitionPlan plan, CancellationToken cancellationToken)
    {
        if (plan.StudentId is { } studentId) return [studentId];
        if (plan.ClassId is not { } classId) return [];
        return await dbContext.Students.AsNoTracking()
            .Where(x => x.ClassId == classId && x.IsActive && !x.IsDeleted)
            .Select(x => x.Id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Taksitleri yeniden uretir. ODENMIS (PaidCents > 0) satirlara dokunulmaz: plan
    /// duzeltilirken tahsilat gecmisi silinirse kasa ile borc birbirini tutmaz.
    /// </summary>
    private async Task RebuildInstallmentsAsync(TuitionPlan plan, IReadOnlyList<Guid> students,
        IReadOnlyList<PlannedInstallment> planned, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await dbContext.TuitionInstallments.Where(x => x.PlanId == plan.Id).ToListAsync(cancellationToken);
        var paid = existing.Where(x => x.PaidCents > 0).ToList();
        dbContext.RemoveRange(existing.Where(x => x.PaidCents == 0));

        // Plan toplami her ogrenci icin ayni; pesinat da bu toplamin bir parcasidir.
        var planTotal = planned.Sum(x => x.AmountCents);
        foreach (var studentId in students)
        {
            var studentPaid = paid.Where(x => x.StudentId == studentId).ToList();
            var rows = planned.Where(row => studentPaid.All(x => x.Sequence != row.Sequence)).ToList();
            // ODENMIS taksitler eski tutarinda kalir (tahsilat gecmisi bozulmamali), ama
            // aradaki fark KALAN taksitlere dagitilir: aksi halde taksit toplami plan
            // tutarindan ayrisiyordu ve fark hicbir ekranda gorunmuyordu. 48.000 -> 60.000
            // zamminda 1.200 TL hic tahsil edilmiyor, ters yonde veliden fazla isteniyordu.
            var adjustments = DistributeRemainder(planTotal - studentPaid.Sum(x => x.AmountCents), rows);
            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                dbContext.Add(new TuitionInstallment
                {
                    PlanId = plan.Id,
                    StudentId = studentId,
                    Sequence = row.Sequence,
                    DueOn = row.DueOn,
                    AmountCents = adjustments[index],
                    Note = row.Note,
                    CreatedAt = now
                });
            }
        }
    }

    /// <summary>
    /// <paramref name="target"/> kurusu <paramref name="rows"/> satirlarina, her satirin
    /// plandaki agirligi oraninda dagitir; kurus artigi ILK satira eklenir ki toplam
    /// birebir tutsun (TuitionSchedule.Build ile ayni kural).
    /// </summary>
    private static long[] DistributeRemainder(long target, List<PlannedInstallment> rows)
    {
        if (rows.Count == 0) return [];
        // Hedef negatifse (odenmis taksitler yeni plan tutarini asiyor) kalan taksitler
        // sifirlanir: veliden eksi borc istenmez.
        if (target <= 0) return [.. rows.Select(_ => 0L)];
        var planned = rows.Sum(x => x.AmountCents);
        var result = new long[rows.Count];
        if (planned <= 0)
        {
            // Plandaki agirliklar sifirsa esit bol.
            var each = target / rows.Count;
            for (var index = 0; index < rows.Count; index++) result[index] = each;
            result[0] += target - (each * rows.Count);
            return result;
        }
        var distributed = 0L;
        for (var index = 0; index < rows.Count; index++)
        {
            var share = (long)Math.Round((decimal)target * rows[index].AmountCents / planned,
                MidpointRounding.AwayFromZero);
            result[index] = share;
            distributed += share;
        }
        result[0] += target - distributed;
        return result;
    }

    private async Task<TuitionPlanDetails> DetailsAsync(TuitionPlan plan, DateOnly today,
        CancellationToken cancellationToken, Guid? onlyStudentId = null)
    {
        var className = plan.ClassId is null ? null : await dbContext.Set<SchoolClass>().AsNoTracking()
            .Where(x => x.Id == plan.ClassId).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken);
        var student = plan.StudentId is null ? null : await dbContext.Students.AsNoTracking()
            .Where(x => x.Id == plan.StudentId)
            .Select(x => new { x.StudentNo, Name = x.FirstName + " " + x.LastName }).SingleOrDefaultAsync(cancellationToken);

        var query = dbContext.TuitionInstallments.AsNoTracking().Where(x => x.PlanId == plan.Id);
        if (onlyStudentId is { } filterStudent) query = query.Where(x => x.StudentId == filterStudent);
        var rows = (await query.ToListAsync(cancellationToken))
            .OrderBy(x => x.DueOn).ThenBy(x => x.Sequence).Select(x => Map(x, today)).ToList();

        var live = rows.Where(x => !x.IsCancelled).ToList();
        return new TuitionPlanDetails(
            plan.Id,
            plan.StudentId is null ? TuitionScopes.Class : TuitionScopes.Student,
            plan.ClassId, className, plan.StudentId, student?.Name, student?.StudentNo,
            plan.Kind, TuitionPlanKinds.Label(plan.Kind), plan.Period,
            StudentBalanceService.ToLira(plan.AmountCents), StudentBalanceService.ToLira(plan.DownPaymentCents),
            plan.InstallmentCount, plan.DueDayOfMonth, plan.StartsOn, plan.Note, plan.IsActive,
            live.Sum(x => x.Amount), live.Sum(x => x.Paid), live.Sum(x => x.Remaining),
            live.Where(x => x.Status == TuitionInstallmentStatuses.Overdue).Sum(x => x.Remaining),
            rows);
    }

    private static TuitionInstallmentDetails Map(TuitionInstallment row, DateOnly today)
    {
        var status = TuitionSchedule.StatusOf(row.AmountCents, row.PaidCents, row.IsCancelled, row.DueOn, today);
        var remaining = Math.Max(row.AmountCents - row.PaidCents, 0);
        return new TuitionInstallmentDetails(row.Id, row.PlanId, row.StudentId, row.Sequence, row.DueOn,
            StudentBalanceService.ToLira(row.AmountCents), StudentBalanceService.ToLira(row.PaidCents),
            StudentBalanceService.ToLira(row.IsCancelled ? 0 : remaining),
            status, TuitionInstallmentStatuses.Label(status), row.IsCancelled, row.Note);
    }
}
