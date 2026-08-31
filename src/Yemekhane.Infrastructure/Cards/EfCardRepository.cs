using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Audit;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.Infrastructure.Cards;

public sealed class EfCardRepository(YemekhaneDbContext dbContext, IAuditService auditService) : ICardRepository
{
    public EfCardRepository(YemekhaneDbContext dbContext)
        : this(dbContext, new AuditService(new EfAuditRepository(dbContext, TimeProvider.System), new SystemAuditContext())) { }
    public Task<CardDetails?> FindByNumberAsync(string cardNumber, CancellationToken cancellationToken) =>
        Project(dbContext.StudentCards.AsNoTracking().Where(x => x.CardNumber == cardNumber))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CardDetails>> GetHistoryAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var history = await Project(dbContext.StudentCards.AsNoTracking().Where(x => x.StudentId == studentId))
            .ToListAsync(cancellationToken);
        return history.OrderByDescending(x => x.ValidFrom).ToArray();
    }

    public async Task<CardDetails> AssignAsync(Guid studentId, string cardNumber, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await EnsureStudentAndCardAvailable(studentId, cardNumber, cancellationToken);
        if (await dbContext.StudentCards.AnyAsync(x => x.StudentId == studentId && x.IsActive, cancellationToken))
            throw new EntityConflictException("Öğrencinin aktif kartı var; kart değiştirme işlemini kullanın.");
        var card = CreateCard(studentId, cardNumber, effectiveAt);
        dbContext.StudentCards.Add(card);
        LocalOutbox.Enqueue(dbContext, card, LocalOutbox.UpdateCard, card, timestamp: effectiveAt);
        auditService.Record(new AuditEntry("CardAssigned", nameof(StudentCard), card.Id.ToString(), "Öğrenciye kart atandı.", After: card));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return await GetRequired(card.Id, cancellationToken);
    }

    public async Task<CardDetails> ReplaceAsync(Guid studentId, string cardNumber, string reason, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await EnsureStudentAndCardAvailable(studentId, cardNumber, cancellationToken);
        var activeCards = await dbContext.StudentCards.Where(x => x.StudentId == studentId && x.IsActive).ToListAsync(cancellationToken);
        if (activeCards.Count == 0) throw new EntityNotFoundException("Öğrencinin değiştirilecek aktif kartı bulunamadı.");
        foreach (var oldCard in activeCards) { oldCard.IsActive = false; oldCard.ValidTo = effectiveAt; oldCard.ReplacementReason = reason; oldCard.UpdatedAt = effectiveAt; }
        var card = CreateCard(studentId, cardNumber, effectiveAt);
        dbContext.StudentCards.Add(card);
        foreach (var oldCard in activeCards)
            LocalOutbox.Enqueue(dbContext, oldCard, LocalOutbox.UpdateCard, oldCard, timestamp: effectiveAt);
        LocalOutbox.Enqueue(dbContext, card, LocalOutbox.UpdateCard, card, timestamp: effectiveAt);
        auditService.Record(new AuditEntry("CardReplaced", nameof(StudentCard), card.Id.ToString(), "Öğrenci kartı değiştirildi.",
            activeCards.Count + 1, activeCards.Select(Snapshot).ToArray(), Snapshot(card)));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return await GetRequired(card.Id, cancellationToken);
    }

    public async Task<bool> DeactivateAsync(Guid cardId, string reason, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        var card = await dbContext.StudentCards.SingleOrDefaultAsync(x => x.Id == cardId && x.IsActive, cancellationToken);
        if (card is null) return false;
        var before = Snapshot(card);
        card.IsActive = false; card.ValidTo = effectiveAt; card.ReplacementReason = reason; card.UpdatedAt = effectiveAt;
        LocalOutbox.Enqueue(dbContext, card, LocalOutbox.UpdateCard, card, timestamp: effectiveAt);
        auditService.Record(new AuditEntry("CardDeactivated", nameof(StudentCard), card.Id.ToString(), "Kart pasifleştirildi.", Before: before, After: card));
        await dbContext.SaveChangesAsync(cancellationToken); return true;
    }

    private async Task EnsureStudentAndCardAvailable(Guid studentId, string cardNumber, CancellationToken cancellationToken)
    {
        if (!await dbContext.Students.AnyAsync(x => x.Id == studentId && x.IsActive, cancellationToken))
            throw new EntityNotFoundException("Aktif öğrenci bulunamadı.");
        if (await dbContext.StudentCards.AnyAsync(x => x.CardNumber == cardNumber, cancellationToken))
            throw new EntityConflictException("Kart No daha önce sisteme tanımlanmış.");
    }

    private Task<CardDetails> GetRequired(Guid id, CancellationToken cancellationToken) =>
        Project(dbContext.StudentCards.AsNoTracking().Where(x => x.Id == id)).SingleAsync(cancellationToken);

    private IQueryable<CardDetails> Project(IQueryable<StudentCard> cards) =>
        from card in cards
        join student in dbContext.Students.AsNoTracking() on card.StudentId equals student.Id
        select new CardDetails(card.Id, student.Id, student.StudentNo, student.FirstName + " " + student.LastName,
            card.CardNumber, card.ValidFrom, card.ValidTo, card.ReplacementReason, card.IsActive);

    private static StudentCard CreateCard(Guid studentId, string cardNumber, DateTimeOffset effectiveAt) => new()
    {
        StudentId = studentId, CardNumber = cardNumber, ValidFrom = effectiveAt, IsActive = true
    };

    private static object Snapshot(StudentCard x) => new { x.Id, x.StudentId, x.CardNumber, x.ValidFrom, x.ValidTo, x.ReplacementReason, x.IsActive };
}
