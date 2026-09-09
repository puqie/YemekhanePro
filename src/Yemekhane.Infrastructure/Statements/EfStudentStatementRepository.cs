using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Balances;
using Yemekhane.Application.Statements;
using Yemekhane.Application.Tuition;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Statements;

/// <summary>
/// Ekstreyi dort defteri birlestirerek kurar: tahsilatlar, bakiye hareketleri, taksitler
/// ve yemek kullanimi.
///
/// Tarih karsilastirmasi ve siralama BELLEKTE yapilir: SQLite DateTimeOffset sutununda
/// ORDER BY ceviremiyor, ayrica gun sinirlari Istanbul saatine gore hesaplanmali (UTC'ye
/// gore filtrelenirse gece yarisi civarindaki tahsilat yanlis gune duser).
/// </summary>
public sealed class EfStudentStatementRepository(YemekhaneDbContext dbContext) : IStudentStatementRepository
{
    public async Task<StudentStatement?> BuildAsync(StudentStatementQuery query, DateOnly today, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var student = await dbContext.Students.AsNoTracking().Where(x => x.Id == query.StudentId)
            .Select(x => new { x.Id, x.StudentNo, Name = x.FirstName + " " + x.LastName, x.ClassId, x.SectionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (student is null) return null;

        var className = student.ClassId is null ? null : await dbContext.Set<SchoolClass>().AsNoTracking()
            .Where(x => x.Id == student.ClassId).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken);
        var sectionName = student.SectionId is null ? null : await dbContext.Set<Section>().AsNoTracking()
            .Where(x => x.Id == student.SectionId).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken);
        var parent = await dbContext.Set<Parent>().AsNoTracking()
            .Where(x => x.StudentId == student.Id && x.IsActive)
            .Select(x => new { x.Name, x.NormalizedPhone }).FirstOrDefaultAsync(cancellationToken);

        var lines = new List<StatementLine>();

        // 1) Tahsilatlar (iptal edilenler de gorunur; veli neyin iptal edildigini sorabilir).
        var incomeTypes = await dbContext.Set<IncomeType>().AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var payments = await dbContext.Set<IncomeTransaction>().AsNoTracking()
            .Where(x => x.StudentId == student.Id).ToListAsync(cancellationToken);
        decimal totalPaid = 0, totalVoided = 0;
        foreach (var row in payments.Where(x => InRange(x.TransactionAt, query)))
        {
            var typeName = incomeTypes.TryGetValue(row.IncomeTypeId, out var name) ? name : "Gelir";
            if (row.IsVoided) totalVoided += row.Amount; else totalPaid += row.Amount;
            lines.Add(new StatementLine(StatementSections.Payment, row.TransactionAt, typeName, row.Description,
                row.Amount, 0, row.IsVoided ? "İptal" : "Tahsil edildi", row.IsVoided));
        }

        // 2) Bakiye defteri. Guncel bakiye TUM satirlardan hesaplanir (aralikla sinirli degil):
        // veli "su an ne kadar param var" diye sorar, aralik disi yuklemeler de bakiyeye dahildir.
        var allBalance = await dbContext.StudentBalanceEntries.AsNoTracking()
            .Where(x => x.StudentId == student.Id).ToListAsync(cancellationToken);
        var totals = await Balances.BalanceLedgerQueries.TotalsAsync(dbContext, student.Id, today, cancellationToken);
        decimal topUps = 0, spent = 0;
        foreach (var row in allBalance.Where(x => InRange(x.OccurredAt, query)))
        {
            var amount = StudentBalanceService.ToLira(row.AmountCents);
            if (amount >= 0) topUps += amount; else spent += -amount;
            lines.Add(new StatementLine(StatementSections.Balance, row.OccurredAt,
                BalanceKindLabel(row.Kind), row.Note, amount, 0, BalanceKindLabel(row.Kind), false));
        }

        // 3) Taksitler: vadesi aralikta olanlar listelenir, borc ozeti TUM taksitlerden gelir
        // (gecmis yilin kalan borcu da gorunmeli).
        var installments = await dbContext.TuitionInstallments.AsNoTracking()
            .Where(x => x.StudentId == student.Id).ToListAsync(cancellationToken);
        decimal due = 0, paid = 0, outstanding = 0, overdue = 0;
        foreach (var row in installments.Where(x => !x.IsCancelled))
        {
            due += StudentBalanceService.ToLira(row.AmountCents);
            paid += StudentBalanceService.ToLira(row.PaidCents);
            var remaining = StudentBalanceService.ToLira(Math.Max(row.AmountCents - row.PaidCents, 0));
            outstanding += remaining;
            if (TuitionSchedule.StatusOf(row.AmountCents, row.PaidCents, row.IsCancelled, row.DueOn, today)
                == TuitionInstallmentStatuses.Overdue) overdue += remaining;
        }
        foreach (var row in installments.Where(x => x.DueOn >= query.From && x.DueOn <= query.To))
        {
            var status = TuitionSchedule.StatusOf(row.AmountCents, row.PaidCents, row.IsCancelled, row.DueOn, today);
            var title = row.Sequence == 0 ? "Peşinat" : $"{row.Sequence}. taksit";
            var detail = row.PaidCents > 0 && row.PaidCents < row.AmountCents
                ? $"Ödenen {StudentBalanceService.ToLira(row.PaidCents):N2} ₺, kalan {StudentBalanceService.ToLira(row.AmountCents - row.PaidCents):N2} ₺"
                : row.Note;
            lines.Add(new StatementLine(StatementSections.Tuition,
                new DateTimeOffset(row.DueOn.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), title, detail,
                StudentBalanceService.ToLira(row.AmountCents), 0,
                TuitionInstallmentStatuses.Label(status), row.IsCancelled));
        }

        // 4) Yemek kullanimi.
        var mealNames = await dbContext.Set<MealType>().AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var usages = await dbContext.Set<MealUsage>().AsNoTracking()
            .Where(x => x.StudentId == student.Id).ToListAsync(cancellationToken);
        var meals = 0;
        foreach (var row in usages.Where(x => InRange(x.UsedAt, query)))
        {
            meals++;
            lines.Add(new StatementLine(StatementSections.Meal, row.UsedAt,
                mealNames.TryGetValue(row.MealTypeId, out var name) ? name : "Öğün", null, 0, 1, "Kullanıldı", false));
        }

        var ordered = lines.OrderBy(x => x.OccurredAt).ThenBy(x => x.Section, StringComparer.Ordinal).ToList();
        var sections = ordered.GroupBy(x => x.Section)
            .Select(g => new StatementSectionSummary(g.Key, StatementSections.Label(g.Key), g.Count(),
                g.Key == StatementSections.Meal ? g.Sum(x => x.Quantity) : g.Where(x => !x.IsCancelled).Sum(x => x.Amount)))
            .OrderBy(x => Order(x.Section)).ToList();

        return new StudentStatement(student.Id, student.StudentNo, student.Name, className, sectionName,
            parent?.Name, parent?.NormalizedPhone, query.From, query.To,
            totalPaid, totalVoided, topUps, spent, StudentBalanceService.ToLira(totals.TotalCents),
            due, paid, outstanding, overdue, meals, sections, ordered);
    }

    /// <summary>Istanbul gunune gore aralik testi; UTC'ye gore filtre gece yarisi kayitlarini kaydirir.</summary>
    private static bool InRange(DateTimeOffset value, StudentStatementQuery query)
    {
        var day = StudentBalanceService.IstanbulDate(value);
        return day >= query.From && day <= query.To;
    }

    private static string BalanceKindLabel(string? kind) => kind switch
    {
        StudentBalanceEntryKinds.TopUp => "Bakiye yükleme",
        StudentBalanceEntryKinds.Deduction => "Öğün düşümü",
        StudentBalanceEntryKinds.Refund => "İade",
        _ => "Düzeltme"
    };

    private static int Order(string section) => section switch
    {
        StatementSections.Payment => 0,
        StatementSections.Tuition => 1,
        StatementSections.Balance => 2,
        _ => 3
    };
}
