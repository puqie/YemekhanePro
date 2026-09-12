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
    public async Task<CardDetails?> FindByNumberAsync(string cardNumber, CancellationToken cancellationToken) =>
        await Project(dbContext.StudentCards.AsNoTracking().Where(x => x.CardNumber == cardNumber))
            .SingleOrDefaultAsync(cancellationToken)
        // Kayip kart elde: uzerinde yalnizca ON yuzdeki baski numarasi okunur; aktif kartlar arasinda aranir.
        ?? await Project(dbContext.StudentCards.AsNoTracking().Where(x => x.IsActive && x.PrintedNumber == cardNumber))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<CardDetails>> GetHistoryAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var history = await Project(dbContext.StudentCards.AsNoTracking().Where(x => x.StudentId == studentId))
            .ToListAsync(cancellationToken);
        return history.OrderByDescending(x => x.ValidFrom).ToArray();
    }

    public async Task<CardDetails> AssignAsync(Guid studentId, string cardNumber, string? printedNumber, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await EnsureStudentActive(studentId, cancellationToken);
        // Ayni ogrencinin PASIF karti yeniden yaziliyorsa kart GERI ACILIR: numara tekil oldugu
        // icin yeni satir acilamaz ve once "Kart No daha once sisteme tanimlanmis" deniyordu --
        // yanlislikla pasife dusen kart bir daha hic kullanilamiyordu (saha: "Kart pasif").
        var card = await FindOwnPassiveCard(studentId, cardNumber, cancellationToken);
        if (card is null) await EnsureCardNumberFree(cardNumber, cancellationToken);
        if (await dbContext.StudentCards.AnyAsync(x => x.StudentId == studentId && x.IsActive, cancellationToken))
            throw new EntityConflictException("Öğrencinin aktif kartı var; kart değiştirme işlemini kullanın.");
        if (card is not null)
        {
            var before = Snapshot(card);
            Reactivate(card, printedNumber, effectiveAt);
            auditService.Record(new AuditEntry("CardReactivated", nameof(StudentCard), card.Id.ToString(), "Pasif kart yeniden aktifleştirildi (numara yeniden atandı).", Before: before, After: Snapshot(card)));
        }
        else
        {
            card = CreateCard(studentId, cardNumber, printedNumber, effectiveAt);
            dbContext.StudentCards.Add(card);
            auditService.Record(new AuditEntry("CardAssigned", nameof(StudentCard), card.Id.ToString(), "Öğrenciye kart atandı.", After: card));
        }
        LocalOutbox.Enqueue(dbContext, card, LocalOutbox.UpdateCard, card, timestamp: effectiveAt);
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return await GetRequired(card.Id, cancellationToken);
    }

    public async Task<CardDetails> ReplaceAsync(Guid studentId, string cardNumber, string? printedNumber, string reason, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await EnsureStudentActive(studentId, cancellationToken);
        // Eski karta GERI DONUS: numara ayni ogrencinin pasif kartiysa o kart yeniden aktif olur.
        var card = await FindOwnPassiveCard(studentId, cardNumber, cancellationToken);
        if (card is null) await EnsureCardNumberFree(cardNumber, cancellationToken);
        var activeCards = await dbContext.StudentCards.Where(x => x.StudentId == studentId && x.IsActive).ToListAsync(cancellationToken);
        if (activeCards.Count == 0) throw new EntityNotFoundException("Öğrencinin değiştirilecek aktif kartı bulunamadı.");
        foreach (var oldCard in activeCards) { oldCard.IsActive = false; oldCard.ValidTo = effectiveAt; oldCard.ReplacementReason = reason; oldCard.UpdatedAt = effectiveAt; }
        if (card is not null)
        {
            // ONCE eski kartlar pasife yazilir: ogrenci basina tek aktif kart tekil indeksi
            // ayni toplu yazmada gecici olarak ihlal ediliyordu (SQLite Error 19). Iki yazma da
            // ayni islem (transaction) icinde kalir; yarim sonuc olusmaz.
            await dbContext.SaveChangesAsync(cancellationToken);
            Reactivate(card, printedNumber, effectiveAt);
        }
        else
        {
            card = CreateCard(studentId, cardNumber, printedNumber, effectiveAt);
            dbContext.StudentCards.Add(card);
        }
        foreach (var oldCard in activeCards)
            LocalOutbox.Enqueue(dbContext, oldCard, LocalOutbox.UpdateCard, oldCard, timestamp: effectiveAt);
        LocalOutbox.Enqueue(dbContext, card, LocalOutbox.UpdateCard, card, timestamp: effectiveAt);
        auditService.Record(new AuditEntry("CardReplaced", nameof(StudentCard), card.Id.ToString(), "Öğrenci kartı değiştirildi.",
            activeCards.Count + 1, activeCards.Select(Snapshot).ToArray(), Snapshot(card)));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return await GetRequired(card.Id, cancellationToken);
    }

    public async Task<CardDetails?> SetPrintedNumberAsync(Guid studentId, string? printedNumber, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        var card = await dbContext.StudentCards.SingleOrDefaultAsync(x => x.StudentId == studentId && x.IsActive, cancellationToken);
        if (card is null) return null;
        var before = Snapshot(card);
        card.PrintedNumber = printedNumber; card.UpdatedAt = effectiveAt;
        LocalOutbox.Enqueue(dbContext, card, LocalOutbox.UpdateCard, card, timestamp: effectiveAt);
        auditService.Record(new AuditEntry("CardPrintedNumberSet", nameof(StudentCard), card.Id.ToString(), "Kartın baskı numarası güncellendi.", Before: before, After: Snapshot(card)));
        await dbContext.SaveChangesAsync(cancellationToken);
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

    /// <summary>
    /// Ogrencinin EN SON pasife dusen kartini geri acar. Saha: kart yanlislikla degistirilip
    /// pasife dusunce turnike "Kart pasif" diyordu; programda geri acacak yer yoktu ve numara
    /// tekil oldugu icin yeniden atanamiyordu da. Aktif kart varken reddedilir (tek aktif kart).
    /// </summary>
    public async Task<CardDetails?> ReactivateLatestAsync(Guid studentId, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        await EnsureStudentActive(studentId, cancellationToken);
        if (await dbContext.StudentCards.AnyAsync(x => x.StudentId == studentId && x.IsActive, cancellationToken))
            throw new EntityConflictException("Öğrencinin zaten aktif kartı var.");
        // Siralama BELLEKTE: SQLite DateTimeOffset ile ORDER BY yapamaz; ogrencinin kart
        // sayisi kucuktur. Pasiflestirme ani (ValidTo) en yeni olan geri acilir.
        var passive = await dbContext.StudentCards.Where(x => x.StudentId == studentId && !x.IsActive).ToListAsync(cancellationToken);
        var card = passive.OrderByDescending(x => x.ValidTo ?? x.ValidFrom).ThenByDescending(x => x.ValidFrom).FirstOrDefault();
        if (card is null) return null;
        var before = Snapshot(card);
        Reactivate(card, printedNumber: null, effectiveAt);
        LocalOutbox.Enqueue(dbContext, card, LocalOutbox.UpdateCard, card, timestamp: effectiveAt);
        auditService.Record(new AuditEntry("CardReactivated", nameof(StudentCard), card.Id.ToString(), "Pasif kart yeniden aktifleştirildi.", Before: before, After: Snapshot(card)));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return await GetRequired(card.Id, cancellationToken);
    }

    /// <summary>Kartlar ekrani: secilen pasif kart kimligiyle geri acilir (en son pasif olan degil, SECILEN).</summary>
    public async Task<CardDetails?> ReactivateAsync(Guid cardId, DateTimeOffset effectiveAt, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var card = await dbContext.StudentCards.SingleOrDefaultAsync(x => x.Id == cardId && !x.IsActive, cancellationToken);
        if (card is null) return null;
        await EnsureStudentActive(card.StudentId, cancellationToken);
        if (await dbContext.StudentCards.AnyAsync(x => x.StudentId == card.StudentId && x.IsActive, cancellationToken))
            throw new EntityConflictException("Öğrencinin zaten aktif kartı var; önce onu pasifleştirin.");
        var before = Snapshot(card);
        Reactivate(card, printedNumber: null, effectiveAt);
        LocalOutbox.Enqueue(dbContext, card, LocalOutbox.UpdateCard, card, timestamp: effectiveAt);
        auditService.Record(new AuditEntry("CardReactivated", nameof(StudentCard), card.Id.ToString(), "Pasif kart Kartlar ekranından yeniden aktifleştirildi.", Before: before, After: Snapshot(card)));
        await dbContext.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return await GetRequired(card.Id, cancellationToken);
    }
    /// <summary>Gecerlilik YENIDEN baslar; eski donem denetim kaydinda (Before) kalir.</summary>
    private static void Reactivate(StudentCard card, string? printedNumber, DateTimeOffset effectiveAt)
    {
        card.IsActive = true; card.ValidTo = null; card.ReplacementReason = null;
        card.ValidFrom = effectiveAt; card.UpdatedAt = effectiveAt;
        if (printedNumber is not null) card.PrintedNumber = printedNumber;
    }

    /// <summary>Numara tekildir: ayni ogrencinin pasif karti en fazla bir satirdir.</summary>
    private Task<StudentCard?> FindOwnPassiveCard(Guid studentId, string cardNumber, CancellationToken cancellationToken) =>
        dbContext.StudentCards.SingleOrDefaultAsync(x => x.CardNumber == cardNumber && x.StudentId == studentId && !x.IsActive, cancellationToken);

    private async Task EnsureStudentActive(Guid studentId, CancellationToken cancellationToken)
    {
        if (!await dbContext.Students.AnyAsync(x => x.Id == studentId && x.IsActive, cancellationToken))
            throw new EntityNotFoundException("Aktif öğrenci bulunamadı.");
    }

    /// <summary>Baska ogrencinin karti -- pasif olsa bile -- verilemez; numara sistem genelinde tekildir.</summary>
    private async Task EnsureCardNumberFree(string cardNumber, CancellationToken cancellationToken)
    {
        if (await dbContext.StudentCards.AnyAsync(x => x.CardNumber == cardNumber, cancellationToken))
            throw new EntityConflictException("Kart No daha önce sisteme tanımlanmış.");
    }

    private Task<CardDetails> GetRequired(Guid id, CancellationToken cancellationToken) =>
        Project(dbContext.StudentCards.AsNoTracking().Where(x => x.Id == id)).SingleAsync(cancellationToken);

    private IQueryable<CardDetails> Project(IQueryable<StudentCard> cards) =>
        from card in cards
        join student in dbContext.Students.AsNoTracking() on card.StudentId equals student.Id
        select new CardDetails(card.Id, student.Id, student.StudentNo, student.FirstName + " " + student.LastName,
            card.CardNumber, card.ValidFrom, card.ValidTo, card.ReplacementReason, card.IsActive, card.PrintedNumber);

    private static StudentCard CreateCard(Guid studentId, string cardNumber, string? printedNumber, DateTimeOffset effectiveAt) => new()
    {
        StudentId = studentId, CardNumber = cardNumber, PrintedNumber = printedNumber, ValidFrom = effectiveAt, IsActive = true
    };

    private static object Snapshot(StudentCard x) => new { x.Id, x.StudentId, x.CardNumber, x.PrintedNumber, x.ValidFrom, x.ValidTo, x.ReplacementReason, x.IsActive };
}
