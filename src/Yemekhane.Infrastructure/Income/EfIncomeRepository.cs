using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.Infrastructure.Income;

public sealed class EfIncomeRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider, IAuditService auditService) : IIncomeRepository
{
    public EfIncomeRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider)
        : this(dbContext, timeProvider, new AuditService(new Audit.EfAuditRepository(dbContext, timeProvider), new Audit.SystemAuditContext())) { }
    public async Task<IReadOnlyList<IncomeTypeDetails>> ListTypesAsync(bool includeInactive, CancellationToken cancellationToken) =>
        await dbContext.Set<IncomeType>().AsNoTracking().Where(x => includeInactive || x.IsActive).OrderBy(x => x.Name)
            .Select(x => new IncomeTypeDetails(x.Id, x.Name, x.IsActive)).ToListAsync(cancellationToken);

    public Task<IncomeTypeDetails?> GetTypeAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<IncomeType>().AsNoTracking().Where(x => x.Id == id)
            .Select(x => new IncomeTypeDetails(x.Id, x.Name, x.IsActive)).SingleOrDefaultAsync(cancellationToken);

    public Task<bool> TypeNameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.Set<IncomeType>().AnyAsync(x => x.Name == name &&
            (!excludingId.HasValue || x.Id != excludingId), cancellationToken);

    public async Task<IncomeTypeDetails> AddTypeAsync(SaveIncomeTypeRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var type = new IncomeType { Name = request.Name, IsActive = request.IsActive };
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
        type.Name = request.Name; type.IsActive = request.IsActive; type.UpdatedAt = timeProvider.GetUtcNow();
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
        if (!await dbContext.Set<IncomeType>().AnyAsync(x => x.Id == request.IncomeTypeId && x.IsActive, cancellationToken))
            throw new EntityNotFoundException("Aktif gelir türü bulunamadı.");
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
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (await GetTransactionAsync(item.Id, cancellationToken))!;
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
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetTransactionAsync(id, cancellationToken);
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
        join student in dbContext.Students.AsNoTracking() on item.StudentId equals student.Id into students
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

    private static IncomeTypeDetails Map(IncomeType type) => new(type.Id, type.Name, type.IsActive);
}
