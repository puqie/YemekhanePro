using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Cards;

/// <summary>
/// Kartlar ekraninin listesi: TUM kartlar (aktif + pasif), arama, durum suzgeci ve sayfalama.
///
/// <para>
/// Silinmis/pasif ogrencinin karti da listelenir (IgnoreQueryFilters): operator "bu kart kimin"
/// sorusuna tam yanit almali; satirda ogrencinin pasif oldugu ayrica soylenir. Aktif/pasif
/// sayaclari suzgecten BAGIMSIZDIR: baslikta "312 aktif, 14 pasif" hep gorunur.
/// </para>
/// <para>
/// Siralama ogrenci numarasi + kart numarasidir: SQLite DateTimeOffset ile ORDER BY yapamaz,
/// tarih sirasi istenseydi JulianDay gerekirdi; operator zaten ogrenciyi arar.
/// </para>
/// </summary>
public sealed class EfCardListQuery(YemekhaneDbContext db) : ICardListQuery
{
    public async Task<CardListResult> ListAsync(CardListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1) throw new RequestValidationException("Sayfa numarası en az 1 olmalıdır.");
        if (query.PageSize is < 1 or > CardListQuery.MaximumPageSize)
            throw new RequestValidationException($"Sayfa boyutu 1-{CardListQuery.MaximumPageSize} aralığında olmalıdır.");

        var rows =
            from card in db.StudentCards.AsNoTracking()
            join student in db.Students.IgnoreQueryFilters().AsNoTracking() on card.StudentId equals student.Id
            select new { card, student };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Ogrenciler ve cihaz kart listesiyle AYNI kural: numara, kart no ve baski no BASTAN,
            // ad Turkce normallestirilmis SearchName uzerinden (ad basi ya da " soyad" basi) eslesir.
            var term = query.Search.Trim();
            var normalized = TurkishSearchText.Normalize(term);
            var lastNameTerm = " " + normalized;
            rows = rows.Where(x => x.student.StudentNo.StartsWith(term)
                || x.card.CardNumber.StartsWith(term)
                || (x.card.PrintedNumber != null && x.card.PrintedNumber.StartsWith(term))
                || x.student.SearchName.StartsWith(normalized)
                || x.student.SearchName.Contains(lastNameTerm));
        }
        if (query.IsActive is { } isActive) rows = rows.Where(x => x.card.IsActive == isActive);

        var activeCount = await db.StudentCards.AsNoTracking().CountAsync(x => x.IsActive, cancellationToken);
        var passiveCount = await db.StudentCards.AsNoTracking().CountAsync(x => !x.IsActive, cancellationToken);
        var total = await rows.CountAsync(cancellationToken);
        var items = await rows
            .OrderBy(x => x.student.StudentNo).ThenBy(x => x.card.CardNumber)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new CardListRow(x.card.Id, x.student.Id, x.student.StudentNo,
                x.student.FirstName + " " + x.student.LastName,
                db.Set<SchoolClass>().Where(c => c.Id == x.student.ClassId).Select(c => c.Name).FirstOrDefault(),
                x.card.CardNumber, x.card.PrintedNumber, x.card.IsActive, x.card.ValidFrom, x.card.ValidTo,
                x.card.ReplacementReason, x.student.IsActive && !x.student.IsDeleted))
            .ToListAsync(cancellationToken);
        return new CardListResult(items, query.Page, query.PageSize, total, activeCount, passiveCount);
    }
}
