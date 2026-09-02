using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Devices;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Devices;

/// <summary>
/// Bir cihazin kart listesi: kart-cihaz durum tablosundan (device_card_states) okunur.
///
/// "Removed" satirlar listelenmez: kart cihazdan silinmistir, artik "cihazdaki kart" degildir.
/// Silinmis ogrencinin karti cihazda hala duruyor olabilir; bu yuzden ogrenci sorgu filtresi
/// (IsDeleted) kaldirilir -- aksi halde cihazda var olan kart ekranda gorunmez, operator
/// "cihazda ne var" sorusuna eksik yanit alirdi.
/// </summary>
public sealed class EfDeviceCardListQuery(YemekhaneDbContext db) : IDeviceCardListQuery
{
    public async Task<DeviceCardListResult> ListAsync(DeviceCardListQuery query, CancellationToken cancellationToken)
    {
        if (query.Page < 1) throw new RequestValidationException("Sayfa numarası en az 1 olmalıdır.");
        if (query.PageSize is < 1 or > DeviceCardListQuery.MaximumPageSize)
            throw new RequestValidationException($"Sayfa boyutu 1-{DeviceCardListQuery.MaximumPageSize} aralığında olmalıdır.");

        var rows =
            from state in db.DeviceCardStates.AsNoTracking()
            join student in db.Students.IgnoreQueryFilters().AsNoTracking() on state.StudentId equals student.Id
            where state.DeviceId == query.DeviceId && state.Status != DeviceCardSyncStatus.Removed
            select new { state, student };

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            // Ogrenciler ekraniyla ayni kural: numara ve kart BASTAN, ad Turkce normallestirilmis
            // SearchName uzerinden (ad basi veya " soyad" basi) eslesir. %5009% gibi ortadan arama
            // 8350090-8350099 kartlarini da getirip tek ogrenci arayan operatoru yaniltiyordu.
            var term = query.Search.Trim();
            var normalized = TurkishSearchText.Normalize(term);
            var lastNameTerm = " " + normalized;
            rows = rows.Where(x => x.student.StudentNo.StartsWith(term)
                || x.state.CardNumber.StartsWith(term)
                || x.student.SearchName.StartsWith(normalized)
                || x.student.SearchName.Contains(lastNameTerm));
        }

        var total = await rows.CountAsync(cancellationToken);
        var items = await rows
            .OrderBy(x => x.student.StudentNo).ThenBy(x => x.state.CardNumber)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(x => new DeviceCardListRow(x.state.CardId, x.student.Id, x.student.StudentNo,
                x.student.FirstName + " " + x.student.LastName,
                db.Set<SchoolClass>().Where(c => c.Id == x.student.ClassId).Select(c => c.Name).FirstOrDefault(),
                x.state.CardNumber, x.state.Status, x.state.LastSyncedAt, x.state.AttemptCount, x.state.LastError))
            .ToListAsync(cancellationToken);
        return new DeviceCardListResult(items, query.Page, query.PageSize, total);
    }
}
