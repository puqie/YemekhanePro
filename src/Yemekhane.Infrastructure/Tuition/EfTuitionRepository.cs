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
                await DetailsAsync(own, today, cancellationToken), await PaymentsAsync(own.Id, studentId, cancellationToken));

        var classPlan = student.ClassId is null ? null : (await dbContext.TuitionPlans.AsNoTracking()
            .Where(x => x.ClassId == student.ClassId && x.IsActive).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault();
        return new StudentTuitionSummary(studentId, student.StudentNo, student.Name, className, classPlan is not null,
            classPlan is null ? null : await DetailsAsync(classPlan, today, cancellationToken, studentId),
            classPlan is null ? [] : await PaymentsAsync(classPlan.Id, studentId, cancellationToken));
    }

    /// <summary>Ogrencinin bu plandaki taksitlerine sayilmis tahsilatlar, en yeni ustte.</summary>
    private async Task<IReadOnlyList<TuitionPaymentDetails>> PaymentsAsync(Guid planId, Guid studentId, CancellationToken cancellationToken)
    {
        var rows = await (
            from payment in dbContext.TuitionPayments.AsNoTracking()
            join installment in dbContext.TuitionInstallments.AsNoTracking() on payment.InstallmentId equals installment.Id
            join income in dbContext.Set<IncomeTransaction>().AsNoTracking() on payment.IncomeTransactionId equals income.Id
            join type in dbContext.Set<IncomeType>().AsNoTracking() on income.IncomeTypeId equals type.Id
            where installment.PlanId == planId && installment.StudentId == studentId
            select new { payment.Id, payment.IncomeTransactionId, installment.Sequence, income.TransactionAt, payment.AmountCents, TypeName = type.Name, income.Description })
            .ToListAsync(cancellationToken);
        // SQLite DateTimeOffset'te ORDER BY ceviremez; siralama bellekte.
        return rows.OrderByDescending(x => x.TransactionAt).ThenByDescending(x => x.Sequence)
            .Select(x => new TuitionPaymentDetails(x.Id, x.IncomeTransactionId, x.Sequence, x.TransactionAt,
                StudentBalanceService.ToLira(x.AmountCents), x.TypeName, x.Description))
            .ToList();
    }

    /// <summary>
    /// Anasinifi listesi: sinif turu Anasinifi olan AKTIF ogrenciler ve ayrica gecerli ucret plani
    /// olan aktif ogrenciler (sinif turu isaretlenmemis olsa da plan varsa anasinifi tahsilati izlenir).
    /// Plan/taksit/odeme verisi toplu okunur; ogrenci basina sorgu atilmaz.
    /// </summary>
    public async Task<KindergartenOverview> KindergartenAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var classes = await dbContext.Set<SchoolClass>().AsNoTracking()
            .Select(x => new { x.Id, x.Name, x.Kind }).ToListAsync(cancellationToken);
        var classById = classes.ToDictionary(x => x.Id);
        var preschoolClassIds = classes.Where(x => x.Kind == ClassKinds.Preschool).Select(x => x.Id).ToHashSet();

        var activePlans = await dbContext.TuitionPlans.AsNoTracking().Where(x => x.IsActive).ToListAsync(cancellationToken);
        var studentPlanIds = activePlans.Where(x => x.StudentId is not null).Select(x => x.StudentId!.Value).ToHashSet();
        var classPlanIds = activePlans.Where(x => x.ClassId is not null).Select(x => x.ClassId!.Value).ToHashSet();

        var students = (await dbContext.Students.AsNoTracking()
                .Where(x => x.IsActive && !x.IsDeleted)
                .Select(x => new { x.Id, x.StudentNo, x.FirstName, x.LastName, x.ClassId }).ToListAsync(cancellationToken))
            .Where(x => (x.ClassId is { } classId && (preschoolClassIds.Contains(classId) || classPlanIds.Contains(classId)))
                        || studentPlanIds.Contains(x.Id))
            .OrderBy(x => x.LastName, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), true))
            .ThenBy(x => x.FirstName, StringComparer.Create(new System.Globalization.CultureInfo("tr-TR"), true))
            .ToList();

        var planIds = activePlans.Select(x => x.Id).ToList();
        var installments = await dbContext.TuitionInstallments.AsNoTracking()
            .Where(x => planIds.Contains(x.PlanId)).ToListAsync(cancellationToken);
        var installmentIds = installments.Select(x => x.Id).ToList();
        var payments = await (
            from payment in dbContext.TuitionPayments.AsNoTracking()
            join income in dbContext.Set<IncomeTransaction>().AsNoTracking() on payment.IncomeTransactionId equals income.Id
            where installmentIds.Contains(payment.InstallmentId)
            select new { payment.InstallmentId, payment.IncomeTransactionId, income.TransactionAt })
            .ToListAsync(cancellationToken);
        var paymentsByInstallment = payments.ToLookup(x => x.InstallmentId);

        var rows = new List<KindergartenStudentRow>(students.Count);
        foreach (var student in students)
        {
            var className = student.ClassId is { } cid && classById.TryGetValue(cid, out var cls) ? cls.Name : null;
            var plan = activePlans.Where(x => x.StudentId == student.Id).OrderByDescending(x => x.CreatedAt).FirstOrDefault()
                ?? (student.ClassId is null ? null
                    : activePlans.Where(x => x.ClassId == student.ClassId).OrderByDescending(x => x.CreatedAt).FirstOrDefault());
            if (plan is null)
            {
                rows.Add(new KindergartenStudentRow(student.Id, student.StudentNo, student.FirstName + " " + student.LastName, className,
                    false, 0, 0, 0, 0, 0, 0, 0, 0, null, null));
                continue;
            }

            var live = installments.Where(x => x.PlanId == plan.Id && x.StudentId == student.Id && !x.IsCancelled)
                .OrderBy(x => x.DueOn).ThenBy(x => x.Sequence).ToList();
            var studentPayments = live.SelectMany(x => paymentsByInstallment[x.Id]).ToList();
            var overdue = live.Where(x => TuitionSchedule.StatusOf(x.AmountCents, x.PaidCents, false, x.DueOn, today) == TuitionInstallmentStatuses.Overdue).ToList();
            rows.Add(new KindergartenStudentRow(
                student.Id, student.StudentNo, student.FirstName + " " + student.LastName, className,
                true,
                live.Count,
                live.Count(x => x.PaidCents >= x.AmountCents),
                studentPayments.Select(x => x.IncomeTransactionId).Distinct().Count(),
                StudentBalanceService.ToLira(live.Sum(x => x.AmountCents)),
                StudentBalanceService.ToLira(live.Sum(x => Math.Min(x.PaidCents, x.AmountCents))),
                StudentBalanceService.ToLira(live.Sum(x => Math.Max(x.AmountCents - x.PaidCents, 0))),
                StudentBalanceService.ToLira(overdue.Sum(x => Math.Max(x.AmountCents - x.PaidCents, 0))),
                overdue.Count,
                live.FirstOrDefault(x => x.PaidCents < x.AmountCents)?.DueOn,
                studentPayments.Count == 0 ? null : studentPayments.Max(x => x.TransactionAt)));
        }

        var unapplied = await TuitionIncomeAllocator.Unapplied(dbContext).CountAsync(cancellationToken);
        var hasTuitionType = await dbContext.Set<IncomeType>().AnyAsync(x => x.IsActive && x.CountsTowardTuition, cancellationToken);
        return new KindergartenOverview(rows, unapplied, hasTuitionType);
    }

    /// <summary>
    /// Ogrenci detayindaki tek satirlik odeme ozeti. SAYIM kasadaki TUM ogrenciye bagli
    /// tahsilatlardan gelir (taksit, yemek ucreti, bakiye yuklemesi); bunlarin kaci taksite
    /// sayilmis ayrica bildirilir. Iptal edilen tahsilat sayima ve toplama GIRMEZ, ayri
    /// sayilir -- "3 kez odedi" derken iptal edilmis bir kayit sayilirsa rakam yalan olur.
    /// Taksit kismi plan varsa dolar; plan yoksa HasPlan false doner ve ekran o kismi yazmaz.
    /// </summary>
    public async Task<StudentPaymentSummary?> PaymentSummaryAsync(Guid studentId, DateOnly today, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters: silinen ogrencinin de gecmisi sorulabilmeli (detay paneli acik kalabilir).
        var student = await dbContext.Students.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == studentId)
            .Select(x => new { x.Id, x.StudentNo, Name = x.FirstName + " " + x.LastName, x.ClassId })
            .SingleOrDefaultAsync(cancellationToken);
        if (student is null) return null;

        var incomes = await dbContext.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => x.StudentId == studentId)
            .Select(x => new { x.Id, x.Amount, x.TransactionAt, x.IsVoided })
            .ToListAsync(cancellationToken);
        var live = incomes.Where(x => !x.IsVoided).ToList();
        var liveIds = live.Select(x => x.Id).ToList();
        // Taksite sayilan tahsilat sayisi: ayni tahsilat birden cok taksite bolunmus olabilir,
        // bu yuzden TAHSILAT kimligine gore tekillestirilir.
        var tuitionPaid = liveIds.Count == 0 ? 0 : await dbContext.TuitionPayments.AsNoTracking()
            .Where(x => liveIds.Contains(x.IncomeTransactionId))
            .Select(x => x.IncomeTransactionId).Distinct().CountAsync(cancellationToken);

        var plan = (await dbContext.TuitionPlans.AsNoTracking()
                .Where(x => x.StudentId == studentId && x.IsActive).ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault()
            ?? (student.ClassId is null ? null : (await dbContext.TuitionPlans.AsNoTracking()
                    .Where(x => x.ClassId == student.ClassId && x.IsActive).ToListAsync(cancellationToken))
                .OrderByDescending(x => x.CreatedAt).FirstOrDefault());

        var installments = plan is null ? [] : await dbContext.TuitionInstallments.AsNoTracking()
            .Where(x => x.PlanId == plan.Id && x.StudentId == studentId && !x.IsCancelled)
            .OrderBy(x => x.DueOn).ThenBy(x => x.Sequence).ToListAsync(cancellationToken);

        return new StudentPaymentSummary(
            student.Id, student.StudentNo, student.Name,
            live.Count,
            tuitionPaid,
            incomes.Count - live.Count,
            live.Sum(x => x.Amount),
            live.Count == 0 ? null : live.Max(x => x.TransactionAt),
            plan is not null,
            installments.Count,
            installments.Count(x => x.PaidCents >= x.AmountCents),
            StudentBalanceService.ToLira(installments.Sum(x => Math.Max(x.AmountCents - x.PaidCents, 0))),
            // Sonraki vade: odenmemis EN ERKEN taksit. Gecikmis olani da buraya duser --
            // patronun gormesi gereken tarih "bir sonraki" degil "bekleyen" tarihtir.
            installments.FirstOrDefault(x => x.PaidCents < x.AmountCents
                && TuitionSchedule.StatusOf(x.AmountCents, x.PaidCents, false, x.DueOn, today) != TuitionInstallmentStatuses.Cancelled)?.DueOn);
    }

    /// <summary>
    /// Bu surumden once kasaya girilmis (ya da tur sonradan isaretlenmis) tahsilatlari taksitlere
    /// sayar. Tarih sirasiyla islenir ki ilk odeme ilk taksite gitsin. Tek transaction: yarim kalirsa
    /// hicbiri yazilmaz, yeniden calistirilir.
    /// </summary>
    public async Task<TuitionReconcileResult> ReconcileAsync(DateOnly today, Guid actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var pending = (await TuitionIncomeAllocator.Unapplied(dbContext).AsNoTracking().ToListAsync(cancellationToken))
            .OrderBy(x => x.TransactionAt).ThenBy(x => x.CreatedAt).ToList();
        int applied = 0, skipped = 0;
        foreach (var income in pending)
        {
            var result = await TuitionIncomeAllocator.AllocateAsync(dbContext, income, now, actorId, cancellationToken);
            if (result is { Applied: true }) applied++; else skipped++;
        }
        if (applied > 0)
            auditService.Record(new AuditEntry("TuitionPaymentsReconciled", nameof(TuitionPayment), Guid.Empty.ToString(),
                $"Kasadaki {applied} tahsilat taksitlere sayıldı ({skipped} tahsilatın açık taksiti yok).",
                After: new { Examined = pending.Count, Applied = applied, Skipped = skipped }));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new TuitionReconcileResult(pending.Count, applied, skipped);
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
