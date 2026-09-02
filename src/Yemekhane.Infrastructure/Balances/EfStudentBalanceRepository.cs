using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Common;
using Yemekhane.Application.Income;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.Infrastructure.Balances;

public sealed class EfStudentBalanceRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider, IAuditService auditService)
    : IStudentBalanceRepository
{
    public EfStudentBalanceRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider)
        : this(dbContext, timeProvider, new AuditService(new Audit.EfAuditRepository(dbContext, timeProvider), new Audit.SystemAuditContext())) { }

    public async Task<Guid?> FindStudentIdAsync(Guid? studentId, string? studentNo, CancellationToken cancellationToken)
    {
        var query = dbContext.Students.AsNoTracking();
        query = studentId.HasValue ? query.Where(x => x.Id == studentId.Value) : query.Where(x => x.StudentNo == studentNo);
        return await query.Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<StudentBalanceSummary?> GetAsync(Guid studentId, DateOnly asOf, int page, int pageSize, CancellationToken cancellationToken)
    {
        var student = await dbContext.Students.AsNoTracking().Where(x => x.Id == studentId)
            .Select(x => new { x.StudentNo, Name = x.FirstName + " " + x.LastName }).SingleOrDefaultAsync(cancellationToken);
        if (student is null) return null;
        // Defter zaten toplam icin bastan sona okunur (ogrenci basina birkac yuz satir); siralama ve
        // sayfalama bellekte yapilir. SQLite DateTimeOffset sutununda ORDER BY ceviremiyor.
        var all = await dbContext.StudentBalanceEntries.AsNoTracking().Where(x => x.StudentId == studentId).ToListAsync(cancellationToken);
        var totals = BalanceLedger.Compute(all.Select(BalanceLedgerQueries.ToLine), asOf);
        var items = all.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(Map).ToList();
        return new StudentBalanceSummary(studentId, student.StudentNo, student.Name,
            StudentBalanceService.ToLira(totals.TotalCents), StudentBalanceService.ToLira(totals.AvailableCents),
            StudentBalanceService.ToLira(totals.ExpiredCents), asOf,
            new PagedResult<StudentBalanceEntryDetails>(items, page, pageSize, all.Count));
    }

    public async Task<BalanceTopUpResult> TopUpAsync(BalanceTopUpCommand command, Guid actorId, CancellationToken cancellationToken)
    {
        // Ayni OperationId ile tekrar (masaustu yeniden denemesi): ikinci yukleme yazilmaz, ilk sonuc doner.
        if (await FindByOperationAsync(command.OperationId, cancellationToken) is { } replay) return replay;

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var incomeType = await EnsureIncomeTypeAsync(cancellationToken);
        var amount = StudentBalanceService.ToLira(command.AmountCents);
        var income = new IncomeTransaction
        {
            OperationId = command.OperationId, StudentId = command.StudentId, IncomeTypeId = incomeType.Id,
            CardNumber = await dbContext.StudentCards.AsNoTracking()
                .Where(x => x.StudentId == command.StudentId && x.IsActive).Select(x => x.CardNumber).FirstOrDefaultAsync(cancellationToken),
            TransactionAt = command.TransactionAt, Amount = amount, Description = command.Note,
            CreatedBy = actorId, CreatedAt = timeProvider.GetUtcNow()
        };
        var entry = new StudentBalanceEntry
        {
            StudentId = command.StudentId, AmountCents = command.AmountCents, Kind = StudentBalanceEntryKinds.TopUp,
            ReferenceType = StudentBalanceReferenceTypes.IncomeTransaction, ReferenceId = income.Id,
            Note = command.Note, OccurredAt = command.TransactionAt, ExpiresOn = command.ExpiresOn, CreatedBy = actorId,
            CreatedAt = income.CreatedAt
        };
        dbContext.AddRange(income, entry);
        LocalOutbox.Enqueue(dbContext, income, LocalOutbox.CreateIncomeTransaction, income, command.OperationId, command.TransactionAt);
        auditService.Record(new AuditEntry("BalanceTopUp", nameof(StudentBalanceEntry), entry.Id.ToString(),
            $"Bakiye yüklendi: {amount:N2} ₺", After: entry, UserId: actorId));
        auditService.Record(new AuditEntry("IncomeCreated", nameof(IncomeTransaction), income.Id.ToString(),
            "Gelir işlemi oluşturuldu (bakiye yükleme).", After: income, UserId: actorId));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            // Es zamanli ayni OperationId: benzersiz indeks kazanani belirledi, onun sonucunu dondur.
            if (await FindByOperationAsync(command.OperationId, cancellationToken) is { } winner) return winner;
            throw;
        }
        return await BuildResultAsync(income.Id, entry, cancellationToken);
    }

    /// <summary>"Bakiye Yükleme" gelir turu yoksa olusturulur; pasifse yeniden acilir (yukleme onsuz kaydedilemez).</summary>
    private async Task<IncomeType> EnsureIncomeTypeAsync(CancellationToken cancellationToken)
    {
        var type = await dbContext.Set<IncomeType>().SingleOrDefaultAsync(x => x.Name == StudentBalanceIncomeType.Name, cancellationToken);
        if (type is null)
        {
            type = new IncomeType { Name = StudentBalanceIncomeType.Name, IsActive = true };
            dbContext.Add(type);
        }
        else if (!type.IsActive)
        {
            type.IsActive = true; type.UpdatedAt = timeProvider.GetUtcNow();
        }
        return type;
    }

    private async Task<BalanceTopUpResult?> FindByOperationAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var incomeId = await dbContext.Set<IncomeTransaction>().AsNoTracking().Where(x => x.OperationId == operationId)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        if (incomeId is null) return null;
        var entry = await dbContext.StudentBalanceEntries.AsNoTracking().SingleOrDefaultAsync(
            x => x.ReferenceType == StudentBalanceReferenceTypes.IncomeTransaction && x.ReferenceId == incomeId && x.Kind == StudentBalanceEntryKinds.TopUp,
            cancellationToken);
        // OperationId bir bakiye yuklemesine degil sade bir gelir kaydina aitse bu bir catisma.
        if (entry is null) throw new EntityConflictException("Bu işlem numarası daha önce farklı bir istek için kullanılmış; kayıt değiştirilmedi.");
        return await BuildResultAsync(incomeId.Value, entry, cancellationToken);
    }

    private async Task<BalanceTopUpResult> BuildResultAsync(Guid incomeId, StudentBalanceEntry entry, CancellationToken cancellationToken)
    {
        var income = await Project(dbContext.Set<IncomeTransaction>().AsNoTracking().Where(x => x.Id == incomeId)).SingleAsync(cancellationToken);
        var totals = await BalanceLedgerQueries.TotalsAsync(dbContext, entry.StudentId,
            StudentBalanceService.IstanbulDate(timeProvider.GetUtcNow()), cancellationToken);
        return new BalanceTopUpResult(income, Map(entry), StudentBalanceService.ToLira(totals.TotalCents), StudentBalanceService.ToLira(totals.AvailableCents));
    }

    private IQueryable<IncomeTransactionDetails> Project(IQueryable<IncomeTransaction> transactions) =>
        from item in transactions
        join type in dbContext.Set<IncomeType>().AsNoTracking() on item.IncomeTypeId equals type.Id
        join student in dbContext.Students.AsNoTracking() on item.StudentId equals student.Id into students
        from student in students.DefaultIfEmpty()
        select new IncomeTransactionDetails(item.Id, item.OperationId, item.StudentId,
            student == null ? null : student.FirstName + " " + student.LastName, student == null ? null : student.StudentNo, item.CardNumber,
            item.TransactionAt, item.IncomeTypeId, type.Name, item.Amount, item.Description, item.CreatedBy,
            item.IsVoided, item.VoidedAt, item.VoidedBy, item.VoidReason);

    private static StudentBalanceEntryDetails Map(StudentBalanceEntry x) => new(x.Id, x.OccurredAt, x.Kind,
        StudentBalanceService.ToLira(x.AmountCents), x.Note, x.ReferenceType, x.ReferenceId, x.ExpiresOn, x.CreatedBy);
}
