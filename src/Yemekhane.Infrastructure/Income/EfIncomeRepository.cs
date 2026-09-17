using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Balances;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Tuition;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.Infrastructure.Income;

public sealed class EfIncomeRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider, IAuditService auditService) : IIncomeRepository
{
    public EfIncomeRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider)
        : this(dbContext, timeProvider, new AuditService(new Audit.EfAuditRepository(dbContext, timeProvider), new Audit.SystemAuditContext())) { }
    public async Task<IReadOnlyList<IncomeTypeDetails>> ListTypesAsync(bool includeInactive, CancellationToken cancellationToken) =>
        await dbContext.Set<IncomeType>().AsNoTracking().Where(x => includeInactive || x.IsActive).OrderBy(x => x.Name)
            .Select(x => new IncomeTypeDetails(x.Id, x.Name, x.IsActive, x.CountsTowardTuition)).ToListAsync(cancellationToken);

    public Task<IncomeTypeDetails?> GetTypeAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<IncomeType>().AsNoTracking().Where(x => x.Id == id)
            .Select(x => new IncomeTypeDetails(x.Id, x.Name, x.IsActive, x.CountsTowardTuition)).SingleOrDefaultAsync(cancellationToken);

    public Task<bool> TypeNameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.Set<IncomeType>().AnyAsync(x => x.Name == name &&
            (!excludingId.HasValue || x.Id != excludingId), cancellationToken);

    public async Task<IncomeTypeDetails> AddTypeAsync(SaveIncomeTypeRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var type = new IncomeType { Name = request.Name, IsActive = request.IsActive, CountsTowardTuition = request.CountsTowardTuition };
        dbContext.Add(type);
        Record(actorId, "IncomeTypeCreated", nameof(IncomeType), type.Id, "Gelir türü oluşturuldu.", null, type);
        await SaveWithConflictAsync("Gelir türü adı zaten kayıtlı.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(type);
    }

    public async Task<IncomeTypeDetails?> UpdateTypeAsync(Guid id, SaveIncomeTypeRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var type = await dbContext.Set<IncomeType>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (type is null) return null;
        var before = Map(type);
        type.Name = request.Name; type.IsActive = request.IsActive; type.CountsTowardTuition = request.CountsTowardTuition;
        type.UpdatedAt = timeProvider.GetUtcNow();
        Record(actorId, "IncomeTypeUpdated", nameof(IncomeType), type.Id, "Gelir türü güncellendi.", before, Map(type));
        await SaveWithConflictAsync("Gelir türü adı zaten kayıtlı.", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(type);
    }

    public async Task<bool> DeactivateTypeAsync(Guid id, Guid actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var type = await dbContext.Set<IncomeType>().SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken);
        if (type is null) return false;
        var before = Map(type);
        type.IsActive = false; type.UpdatedAt = timeProvider.GetUtcNow();
        Record(actorId, "IncomeTypeDeactivated", nameof(IncomeType), type.Id, "Gelir türü pasifleştirildi.", before, Map(type));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IncomeTransactionDetails> CreateTransactionAsync(CreateIncomeTransactionRequest request, Guid actorId,
        CancellationToken cancellationToken)
    {
        var existing = await FindByOperationIdAsync(request.OperationId, cancellationToken);
        // Ayni OperationId farkli bir yukle gelirse sessizce eski kaydi donmek, yazilmayan bir islem icin
        // basari bildirmek olur. BulkOperations'taki RequestHash kalibiyla ayni sekilde catisma bildiriyoruz.
        if (existing is not null) return EnsureSameRequest(existing, request);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var incomeType = await dbContext.Set<IncomeType>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.IncomeTypeId && x.IsActive, cancellationToken)
            ?? throw new EntityNotFoundException("Aktif gelir türü bulunamadı.");
        if (request.StudentId is { } studentId &&
            !await dbContext.Students.AnyAsync(x => x.Id == studentId, cancellationToken))
            throw new EntityNotFoundException("Öğrenci bulunamadı.");

        var item = new IncomeTransaction
        {
            OperationId = request.OperationId, StudentId = request.StudentId, CardNumber = request.CardNumber,
            TransactionAt = request.TransactionAt, IncomeTypeId = request.IncomeTypeId, Amount = request.Amount,
            Description = request.Description, CreatedBy = actorId, CreatedAt = timeProvider.GetUtcNow()
        };
        dbContext.Add(item);
        LocalOutbox.Enqueue(dbContext, item, LocalOutbox.CreateIncomeTransaction, item,
            request.OperationId, request.TransactionAt);
        Record(actorId, "IncomeCreated", nameof(IncomeTransaction), item.Id, "Gelir işlemi oluşturuldu.", null, item);
        // Taksite sayilir turden ogrenci tahsilati ayni transaction'da taksitlere islenir: gelir
        // kaydi yazilip taksit islenmezse okul "kacinci taksit" sorusuna yine cevap alamazdi.
        var (tuitionNote, tuitionWarning) = incomeType.CountsTowardTuition && item.StudentId is not null
            ? await ApplyTuitionAsync(item, actorId, cancellationToken)
            : (null, null);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetTransactionAsync(item.Id, cancellationToken))! with { TuitionNote = tuitionNote, Warning = tuitionWarning };
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            existing = await FindByOperationIdAsync(request.OperationId, cancellationToken);
            if (existing is not null) return existing;
            throw;
        }
    }

    private static IncomeTransactionDetails EnsureSameRequest(IncomeTransactionDetails existing,
        CreateIncomeTransactionRequest request)
    {
        if (existing.Amount != request.Amount
            || existing.IncomeTypeId != request.IncomeTypeId
            || existing.StudentId != request.StudentId
            || existing.TransactionAt != request.TransactionAt)
        {
            throw new EntityConflictException(
                "Bu işlem numarası daha önce farklı bir istek için kullanılmış; kayıt değiştirilmedi.");
        }

        return existing;
    }

    public async Task<IncomeTransactionDetails?> VoidTransactionAsync(Guid id, string reason, Guid actorId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var item = await dbContext.Set<IncomeTransaction>().SingleOrDefaultAsync(x => x.Id == id && !x.IsVoided, cancellationToken);
        if (item is null) return null;
        var before = await GetTransactionAsync(id, cancellationToken);
        item.IsVoided = true; item.VoidedAt = timeProvider.GetUtcNow(); item.VoidedBy = actorId; item.VoidReason = reason;
        Record(actorId, "IncomeVoided", nameof(IncomeTransaction), item.Id, "Gelir işlemi iptal edildi.", before,
            new { item.IsVoided, item.VoidedAt, item.VoidedBy, item.VoidReason });
        var warning = await RefundBalanceTopUpAsync(item, actorId, reason, cancellationToken);
        var tuitionWarning = await ReverseTuitionAsync(item, actorId, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var voided = await GetTransactionAsync(id, cancellationToken);
        var combined = string.Join(" ", new[] { warning, tuitionWarning }.Where(x => x is not null));
        return combined.Length == 0 ? voided : voided! with { Warning = combined };
    }

    /// <summary>
    /// Tahsilati ogrencinin acik taksitlerine vade sirasiyla sayar (bkz. <see cref="TuitionIncomeAllocator"/>).
    /// Plan ya da acik taksit yoksa gelir yine kaydedilir; kasiyer uyariyla bilgilendirilir ki para
    /// "kayboldu" sanilmasin.
    /// </summary>
    private async Task<(string? Note, string? Warning)> ApplyTuitionAsync(IncomeTransaction item, Guid actorId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var result = await TuitionIncomeAllocator.AllocateAsync(dbContext, item, now, actorId, cancellationToken);
        if (result is null) return (null, null);
        if (!result.Applied)
            return (null, "Tahsilat kaydedildi ama taksite sayılmadı: öğrencinin ücret planı ya da açık taksiti yok.");
        var plan = await TuitionIncomeAllocator.EffectivePlanAsync(dbContext, result.StudentId,
            await dbContext.Students.AsNoTracking().Where(x => x.Id == result.StudentId).Select(x => x.ClassId).SingleAsync(cancellationToken),
            cancellationToken);
        var (paid, count) = plan is null ? (0, 0)
            : await TuitionIncomeAllocator.ProgressAsync(dbContext, plan.Id, result.StudentId, cancellationToken);
        var note = result.Describe(paid, count);
        Record(actorId, "TuitionPaymentApplied", nameof(IncomeTransaction), item.Id, "Tahsilat taksitlere sayıldı: " + note, null,
            new { result.Lines, result.Unallocated });
        return (note, result.Unallocated > 0 ? $"{result.Unallocated:N2} ₺ fazla ödeme taksitlere sığmadı; kasada kaldı." : null);
    }

    private async Task<string?> ReverseTuitionAsync(IncomeTransaction item, Guid actorId, CancellationToken cancellationToken)
    {
        var reversed = await TuitionIncomeAllocator.ReverseAsync(dbContext, item.Id, timeProvider.GetUtcNow(), cancellationToken);
        if (reversed == 0) return null;
        Record(actorId, "TuitionPaymentReversed", nameof(IncomeTransaction), item.Id,
            $"İptal nedeniyle {reversed} taksitin ödemesi geri alındı.", null, new { Installments = reversed });
        return $"Taksit ödemesi geri alındı: {reversed} taksit yeniden borçlu.";
    }

    /// <summary>
    /// Iptal edilen islem bir bakiye yuklemesiyse defterine negatif iade yazilir; bakiye zaten
    /// harcanmissa eksiye duser ve kasiyere uyari doner (para gitti, yukleme geri alindi).
    /// Ayni iade iki kez yazilmaz (ayni ReferenceId ile Refund varsa atlanir).
    /// </summary>
    private async Task<string?> RefundBalanceTopUpAsync(IncomeTransaction item, Guid actorId, string reason, CancellationToken cancellationToken)
    {
        var topUp = await dbContext.StudentBalanceEntries.AsNoTracking().SingleOrDefaultAsync(
            x => x.ReferenceType == StudentBalanceReferenceTypes.IncomeTransaction && x.ReferenceId == item.Id && x.Kind == StudentBalanceEntryKinds.TopUp,
            cancellationToken);
        if (topUp is null) return null;
        if (await dbContext.StudentBalanceEntries.AnyAsync(
                x => x.ReferenceId == item.Id && x.Kind == StudentBalanceEntryKinds.Refund, cancellationToken))
            return null;
        var now = timeProvider.GetUtcNow();
        var refund = new StudentBalanceEntry
        {
            StudentId = topUp.StudentId, AmountCents = -topUp.AmountCents, Kind = StudentBalanceEntryKinds.Refund,
            ReferenceType = StudentBalanceReferenceTypes.IncomeTransaction, ReferenceId = item.Id,
            Note = "Yükleme iptal edildi: " + reason, OccurredAt = now, CreatedBy = actorId, CreatedAt = now
        };
        dbContext.Add(refund);
        Record(actorId, "BalanceRefund", nameof(StudentBalanceEntry), refund.Id, "Bakiye yüklemesi iptal nedeniyle geri alındı.", null, refund);
        // Toplam, henuz kaydedilmemis iade dahil hesaplanir; defter satirlari sorguyla okunur.
        var totals = await BalanceLedgerQueries.TotalsAsync(dbContext, topUp.StudentId, StudentBalanceService.IstanbulDate(now), cancellationToken);
        var after = totals.TotalCents - topUp.AmountCents;
        return after < 0
            ? $"Bakiye yüklemesi geri alındı; öğrencinin bakiyesi {StudentBalanceService.ToLira(after):N2} ₺ ile EKSİYE düştü (para daha önce harcanmış)."
            : null;
    }

    public async Task<PagedResult<IncomeTransactionDetails>> ListTransactionsAsync(IncomeTransactionFilter filter,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Set<IncomeTransaction>().FromSqlInterpolated($$"""
            SELECT * FROM income_transactions
            WHERE ({{filter.From}} IS NULL OR julianday(TransactionAt) >= julianday({{filter.From}}))
              AND ({{filter.To}} IS NULL OR julianday(TransactionAt) <= julianday({{filter.To}}))
              AND ({{filter.IncomeTypeId}} IS NULL OR IncomeTypeId = {{filter.IncomeTypeId}})
              AND ({{filter.StudentId}} IS NULL OR StudentId = {{filter.StudentId}})
              AND ({{filter.CardNumber}} IS NULL OR CardNumber = {{filter.CardNumber}})
              AND ({{filter.IsVoided}} IS NULL OR IsVoided = {{filter.IsVoided}})
            """).AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        var offset = (filter.Page - 1) * filter.PageSize;
        var page = dbContext.Set<IncomeTransaction>().FromSqlInterpolated($$"""
            SELECT * FROM income_transactions
            WHERE ({{filter.From}} IS NULL OR julianday(TransactionAt) >= julianday({{filter.From}}))
              AND ({{filter.To}} IS NULL OR julianday(TransactionAt) <= julianday({{filter.To}}))
              AND ({{filter.IncomeTypeId}} IS NULL OR IncomeTypeId = {{filter.IncomeTypeId}})
              AND ({{filter.StudentId}} IS NULL OR StudentId = {{filter.StudentId}})
              AND ({{filter.CardNumber}} IS NULL OR CardNumber = {{filter.CardNumber}})
              AND ({{filter.IsVoided}} IS NULL OR IsVoided = {{filter.IsVoided}})
            ORDER BY julianday(TransactionAt) DESC, Id DESC LIMIT {{filter.PageSize}} OFFSET {{offset}}
            """).AsNoTracking();
        var items = await Project(page).ToListAsync(cancellationToken);
        return new PagedResult<IncomeTransactionDetails>(items, filter.Page, filter.PageSize, total);
    }

    private async Task<IncomeTransactionDetails?> FindByOperationIdAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var id = await dbContext.Set<IncomeTransaction>().AsNoTracking().Where(x => x.OperationId == operationId)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        return id.HasValue ? await GetTransactionAsync(id.Value, cancellationToken) : null;
    }

    private Task<IncomeTransactionDetails?> GetTransactionAsync(Guid id, CancellationToken cancellationToken) =>
        Project(dbContext.Set<IncomeTransaction>().AsNoTracking().Where(x => x.Id == id))
            .SingleOrDefaultAsync(cancellationToken);

    private IQueryable<IncomeTransactionDetails> Project(IQueryable<IncomeTransaction> transactions) =>
        from item in transactions
        join type in dbContext.Set<IncomeType>().AsNoTracking() on item.IncomeTypeId equals type.Id
        // IgnoreQueryFilters: silinen ogrencinin tahsilati kasada KALIR; adi da gorunmelidir.
        // Aksi halde satir adsiz/numarasiz gorunuyor ve "bu para kimin" sorusu yanitsiz kaliyordu.
        join student in dbContext.Students.IgnoreQueryFilters().AsNoTracking() on item.StudentId equals student.Id into students
        from student in students.DefaultIfEmpty()
        select new IncomeTransactionDetails(item.Id, item.OperationId, item.StudentId,
            student == null ? null : student.FirstName + " " + student.LastName, student == null ? null : student.StudentNo, item.CardNumber,
            item.TransactionAt, item.IncomeTypeId, type.Name, item.Amount, item.Description, item.CreatedBy,
            item.IsVoided, item.VoidedAt, item.VoidedBy, item.VoidReason);

    private void Record(Guid actorId, string action, string entityName, Guid entityId, string description, object? before, object? after) =>
        auditService.Record(new AuditEntry(action, entityName, entityId.ToString(), description, Before: before, After: after, UserId: actorId));

    private async Task SaveWithConflictAsync(string message, CancellationToken cancellationToken)
    {
        try { await dbContext.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException) { throw new EntityConflictException(message); }
    }

    private static IncomeTypeDetails Map(IncomeType type) => new(type.Id, type.Name, type.IsActive, type.CountsTowardTuition);
}
