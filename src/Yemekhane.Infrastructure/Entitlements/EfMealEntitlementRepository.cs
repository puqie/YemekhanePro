using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Application.Entitlements;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Access;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.Infrastructure.Entitlements;

public sealed class EfMealEntitlementRepository(YemekhaneDbContext dbContext, IAuditService auditService,
    IAccessCacheInvalidationSink? accessCache = null) : IMealEntitlementRepository
{
    public EfMealEntitlementRepository(YemekhaneDbContext dbContext)
        : this(dbContext, new AuditService(new EfAuditRepository(dbContext, TimeProvider.System), new SystemAuditContext())) { }

    public async Task<BulkEntitlementResult> UpsertBulkAsync(IReadOnlyCollection<Guid> studentIds, Guid mealTypeId,
        IReadOnlyCollection<DateOnly> dates, int quantity, string source, string? expectedStateHash, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var validStudentCount = 0;
        foreach (var chunk in studentIds.Chunk(500))
            validStudentCount += await dbContext.Students.CountAsync(x => chunk.Contains(x.Id) && x.IsActive, cancellationToken);
        if (validStudentCount != studentIds.Count) throw new EntityNotFoundException("Seçilen aktif öğrencilerden en az biri bulunamadı.");
        if (!await dbContext.Set<MealType>().AnyAsync(x => x.Id == mealTypeId && x.IsActive, cancellationToken))
            throw new EntityNotFoundException("Aktif öğün bulunamadı.");

        var existing = await LoadExistingAsync(studentIds, mealTypeId, dates, true, cancellationToken);
        if (expectedStateHash is not null && !string.Equals(expectedStateHash, StateHash(existing), StringComparison.Ordinal))
            throw new EntityConflictException("Önizlemeden sonra hakediş verisi değişti. Yeniden önizleyin.");
        var byKey = existing.ToDictionary(x => (x.StudentId, x.EntitlementDate));
        var created = 0; var updated = 0;
        // Ogrenci basina YENI gun sayisi: ucret bunun uzerinden hesaplanir, aralik
        // uzunlugu uzerinden DEGIL. Zaten var olan hak guncellenirse para tekrar alinmaz.
        var createdPerStudent = new Dictionary<Guid, int>();
        foreach (var studentId in studentIds)
        foreach (var date in dates)
        {
            if (byKey.TryGetValue((studentId, date), out var item))
            {
                if (item.ConsumedQuantity > quantity) throw new EntityConflictException("Kullanılmış hak miktarının altına düşürülemez.");
                item.Quantity = quantity; item.Status = "Active"; item.Source = source; item.Version++;
                item.UpdatedAt = DateTimeOffset.UtcNow; updated++;
            }
            else
            {
                var entitlement = new MealEntitlement { StudentId = studentId, MealTypeId = mealTypeId,
                    EntitlementDate = date, Quantity = quantity, Status = "Active", Source = source };
                dbContext.Add(entitlement);
                LocalOutbox.Enqueue(dbContext, entitlement, LocalOutbox.CreateMealEntitlement, entitlement);
                created++;
                createdPerStudent[studentId] = createdPerStudent.GetValueOrDefault(studentId) + 1;
            }
        }
        var operationId = Guid.NewGuid();
        auditService.Record(new AuditEntry("EntitlementsGranted", nameof(MealEntitlement), operationId.ToString(),
            "Toplu yemek hakkı tanımlandı.", created + updated,
            After: new { StudentCount = studentIds.Count, DateCount = dates.Count, mealTypeId, quantity, source, Created = created, Updated = updated },
            BulkOperationId: operationId));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BulkEntitlementResult(studentIds.Count, dates.Count, created, updated,
            CreatedPerStudent: createdPerStudent);
    }

    /// <summary>
    /// Ogunun birim ucreti; fiyat satiri YOKSA <c>null</c>.
    ///
    /// <para>
    /// Once satir yoksa 0 donuyordu ve "ucretsiz ogun" ile "ucreti TANIMSIZ ogun"
    /// birbirine karisiyordu. Ucretsiz ogun zaten acikca 0 girilerek tanimlanabilir;
    /// satirin hic olmamasi bir EKSIKLIKTIR. Fark edilmedigi icin 250.000 TL'lik bir
    /// hakedis sessizce 0 TL olarak kasaya gecebiliyordu.
    /// </para>
    /// </summary>
    public async Task<decimal?> MealPriceAsync(Guid mealTypeId, CancellationToken cancellationToken)
    {
        var cents = await dbContext.Set<MealTypePrice>().AsNoTracking()
            .Where(x => x.MealTypeId == mealTypeId).Select(x => (long?)x.PriceCents)
            .SingleOrDefaultAsync(cancellationToken);
        return cents is null ? null : cents.Value / 100m;
    }

    public async Task<IReadOnlyList<Guid>> ResolveTargetAsync(EntitlementTarget target, CancellationToken cancellationToken)
    {
        var students = dbContext.Students.AsNoTracking().Where(x => x.IsActive);
        switch (target.Type.Trim().ToLowerInvariant())
        {
            case "manual":
                var ids = (target.StudentIds ?? []).Distinct().ToArray();
                var studentNos = (target.StudentNos ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
                if (ids.Length == 0 && studentNos.Length == 0) throw new RequestValidationException("Manuel hedef için öğrenci seçilmelidir.");
                if (studentNos.Length > 0)
                {
                    // Numara ile verilen ogrenciler: hepsi AKTIF bir ogrenciye karsilik gelmeli.
                    // Yanlis yazilan numara sessizce atlanirsa kullanici "3 ogrenci" beklerken
                    // 2'sine hak tanimlar ve farkina varmaz; bu yuzden eksikler adiyla reddedilir.
                    var found = await dbContext.Students.AsNoTracking().Where(x => x.IsActive && studentNos.Contains(x.StudentNo))
                        .Select(x => new { x.Id, x.StudentNo }).ToListAsync(cancellationToken);
                    var missing = studentNos.Except(found.Select(x => x.StudentNo), StringComparer.Ordinal).ToArray();
                    if (missing.Length > 0)
                        throw new RequestValidationException($"Aktif öğrenci bulunamadı: {string.Join(", ", missing)}");
                    ids = ids.Concat(found.Select(x => x.Id)).Distinct().ToArray();
                }
                students = students.Where(x => ids.Contains(x.Id));
                break;
            case "class":
                if (!target.ClassId.HasValue) throw new RequestValidationException("Sınıf seçilmelidir.");
                students = students.Where(x => x.ClassId == target.ClassId);
                break;
            case "grade":
                if (string.IsNullOrWhiteSpace(target.Grade)) throw new RequestValidationException("Kademe/sınıf seviyesi girilmelidir.");
                var grade = target.Grade.Trim();
                students = students.Where(x => dbContext.Set<SchoolClass>().Any(c => c.Id == x.ClassId && c.Name.StartsWith(grade)));
                break;
            case "group":
                if (!target.GroupId.HasValue) throw new RequestValidationException("Grup seçilmelidir.");
                students = students.Where(x => dbContext.Set<StudentGroupMember>().Any(m => m.GroupId == target.GroupId && m.StudentId == x.Id));
                break;
            case "all":
                break;
            default:
                throw new RequestValidationException("Geçersiz hedef türü.");
        }
        return await students.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync(cancellationToken);
    }

    public async Task<EntitlementPreviewState> PreviewAsync(IReadOnlyCollection<Guid> studentIds, Guid mealTypeId,
        IReadOnlyCollection<DateOnly> dates, CancellationToken cancellationToken)
    {
        if (!await dbContext.Set<MealType>().AnyAsync(x => x.Id == mealTypeId && x.IsActive, cancellationToken))
            throw new EntityNotFoundException("Aktif öğün bulunamadı.");
        var existing = await LoadExistingAsync(studentIds, mealTypeId, dates, false, cancellationToken);
        return new EntitlementPreviewState(studentIds.Count * dates.Count - existing.Count, existing.Count, StateHash(existing));
    }

    public async Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken)
    {
        var rights = dbContext.MealEntitlements.AsNoTracking().AsQueryable();
        if (query.StartsOn.HasValue) rights = rights.Where(x => x.EntitlementDate >= query.StartsOn);
        if (query.EndsOn.HasValue) rights = rights.Where(x => x.EntitlementDate <= query.EndsOn);
        if (query.MealTypeId.HasValue) rights = rights.Where(x => x.MealTypeId == query.MealTypeId);
        if (!string.IsNullOrWhiteSpace(query.Status)) rights = rights.Where(x => x.Status == query.Status.Trim());
        if (query.GroupId.HasValue)
            rights = rights.Where(x => dbContext.Set<StudentGroupMember>().Any(m => m.GroupId == query.GroupId && m.StudentId == x.StudentId));
        if (!string.IsNullOrWhiteSpace(query.StudentNo))
        {
            var studentNo = query.StudentNo.Trim();
            rights = rights.Where(x => dbContext.Students.Any(s => s.Id == x.StudentId && s.StudentNo == studentNo));
        }
        if (!string.IsNullOrWhiteSpace(query.CardNumber))
        {
            var card = query.CardNumber.Trim();
            rights = rights.Where(x => dbContext.StudentCards.Any(c => c.StudentId == x.StudentId && c.IsActive && c.CardNumber == card));
        }
        if (!string.IsNullOrWhiteSpace(query.Name))
        {
            var value = $"%{query.Name.Trim()}%";
            rights = rights.Where(x => dbContext.Students.Any(s => s.Id == x.StudentId
                && EF.Functions.Like(s.FirstName + " " + s.LastName, value)));
        }
        if (!string.IsNullOrWhiteSpace(query.ClassName))
        {
            var value = $"%{query.ClassName.Trim()}%";
            rights = rights.Where(x => dbContext.Students.Any(s => s.Id == x.StudentId
                && dbContext.Set<SchoolClass>().Any(c => c.Id == s.ClassId && EF.Functions.Like(c.Name, value))));
        }
        // TEK ARAMA: ad, ogrenci no, kart no ve sinif adinda BIRDEN arar. Kullanici
        // aradigi seyin hangi alana ait oldugunu bilmek zorunda kalmaz -- once dort
        // ayri kutu vardi ve kart numarasini yanlis kutuya yazan kullanici sessizce
        // bos sonuc aliyordu.
        //
        // Ad ve sinif icin SearchName sutunu kullanilir: ham ada bakilsaydi "ismail"
        // yazan kullanici "İsmail" kaydini BULAMAZDI (Turkce i/I ayrimi).
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var raw = query.Search.Trim();
            var like = $"%{raw}%";
            var normalized = $"%{TurkishSearchText.Normalize(raw)}%";
            rights = rights.Where(x => dbContext.Students.Any(s => s.Id == x.StudentId
                && (EF.Functions.Like(s.SearchName, normalized)
                    || EF.Functions.Like(s.StudentNo, like)
                    || dbContext.Set<SchoolClass>().Any(c => c.Id == s.ClassId
                        && EF.Functions.Like(c.SearchName, normalized))))
                || dbContext.StudentCards.Any(c => c.StudentId == x.StudentId && c.IsActive
                    && EF.Functions.Like(c.CardNumber, like)));
        }

        var joined = from right in rights
                   join student in dbContext.Students.AsNoTracking() on right.StudentId equals student.Id
                   join meal in dbContext.Set<MealType>().AsNoTracking() on right.MealTypeId equals meal.Id
                   select new
                   {
                       Right = right,
                       Student = student,
                       MealName = meal.Name,
                       ClassName = dbContext.Set<SchoolClass>().AsNoTracking()
                           .Where(c => c.Id == student.ClassId).Select(c => c.Name).FirstOrDefault()
                   };

        joined = (query.SortBy.Trim().ToLowerInvariant(), query.Descending) switch
        {
            ("studentno", false) => joined.OrderBy(x => x.Student.StudentNo).ThenBy(x => x.Right.EntitlementDate),
            ("studentno", true) => joined.OrderByDescending(x => x.Student.StudentNo).ThenByDescending(x => x.Right.EntitlementDate),
            ("name", false) => joined.OrderBy(x => x.Student.FirstName).ThenBy(x => x.Student.LastName).ThenBy(x => x.Right.EntitlementDate),
            ("name", true) => joined.OrderByDescending(x => x.Student.FirstName).ThenByDescending(x => x.Student.LastName).ThenByDescending(x => x.Right.EntitlementDate),
            ("meal", false) => joined.OrderBy(x => x.MealName).ThenBy(x => x.Right.EntitlementDate),
            ("meal", true) => joined.OrderByDescending(x => x.MealName).ThenByDescending(x => x.Right.EntitlementDate),
            (_, false) => joined.OrderBy(x => x.Right.EntitlementDate).ThenBy(x => x.Student.StudentNo),
            _ => joined.OrderByDescending(x => x.Right.EntitlementDate).ThenBy(x => x.Student.StudentNo)
        };
        var rows = joined.Select(x => new MealEntitlementListItem(x.Right.Id, x.Student.Id, x.Right.EntitlementDate, x.Student.StudentNo,
                       dbContext.StudentCards.Where(c => c.StudentId == x.Student.Id && c.IsActive)
                           .Select(c => c.CardNumber).FirstOrDefault(),
                       x.MealName, x.Student.FirstName + " " + x.Student.LastName, x.ClassName,
                       x.Right.Quantity, x.Right.ConsumedQuantity,
                       // "Kalan" yalnizca AKTIF hak icin anlamlidir: iptal edilmis / aktarilmis /
                       // yakilmis bir hakkin kullanilabilir kalani yoktur. Onceden iptal satiri
                       // "KALAN 1" gosteriyor ve ozet karti iptalleri kullanilabilir sayiyordu.
                       x.Right.Status == "Active" ? x.Right.Quantity - x.Right.ConsumedQuantity : 0,
                       x.Right.Status, x.Right.Source, x.Right.Version));

        var total = await rows.CountAsync(cancellationToken);
        // Toplam ve kullanilan tum satirlari kapsar (ADET/KULL. sutunlarinin toplamiyla
        // birebir); kalan ise yalnizca aktif satirlardan gelir.
        var totals = await rights.GroupBy(_ => 1).Select(x => new MealEntitlementSummary(
            x.Sum(v => v.Quantity), x.Sum(v => v.ConsumedQuantity),
            x.Sum(v => v.Status == "Active" ? v.Quantity - v.ConsumedQuantity : 0)))
            .SingleOrDefaultAsync(cancellationToken) ?? new MealEntitlementSummary(0, 0, 0);
        var items = await rows.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(cancellationToken);
        return new MealEntitlementPage(items, query.Page, query.PageSize, total, totals);
    }

    public async Task<IReadOnlyList<EntitlementDetails>> ListAsync(Guid studentId, DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken) =>
        await dbContext.MealEntitlements.AsNoTracking().Where(x => x.StudentId == studentId && x.EntitlementDate >= startsOn && x.EntitlementDate <= endsOn)
            .OrderBy(x => x.EntitlementDate).Select(x => new EntitlementDetails(x.Id, x.StudentId, x.MealTypeId, x.EntitlementDate,
                x.Quantity, x.ConsumedQuantity, x.Quantity - x.ConsumedQuantity, x.Status, x.Source)).ToListAsync(cancellationToken);

    /// <summary>
    /// Ogrencinin ogun bazinda acik hakedis donemleri. Gruplama VERITABANINDA degil
    /// hafizada yapilir: SQLite tarafinda DateOnly uzerinde Min/Max toplamasi cevrilemiyor,
    /// ayrica bir ogrencinin hak satiri en fazla birkac yuz tanedir.
    /// </summary>
    public async Task<IReadOnlyList<EntitlementPeriodSummary>> PeriodsAsync(Guid studentId, DateOnly today,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.MealEntitlements.AsNoTracking()
            .Where(x => x.StudentId == studentId && x.Status == "Active")
            .Select(x => new { x.MealTypeId, x.EntitlementDate, x.Quantity, x.ConsumedQuantity })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return [];

        var student = await dbContext.Students.AsNoTracking().Where(x => x.Id == studentId)
            .Select(x => new
            {
                x.StudentNo,
                Name = x.FirstName + " " + x.LastName,
                ClassName = dbContext.Set<SchoolClass>().Where(c => c.Id == x.ClassId).Select(c => c.Name).FirstOrDefault(),
                Phone = dbContext.Set<Parent>().Where(p => p.StudentId == x.Id && p.IsActive)
                    .OrderByDescending(p => p.IsPrimary).Select(p => p.NormalizedPhone).FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (student is null) return [];

        var mealNames = await MealNamesAsync(rows.Select(x => x.MealTypeId), cancellationToken);
        return [.. rows.GroupBy(x => x.MealTypeId)
            .Select(group => Summarize(group.Key, mealNames.GetValueOrDefault(group.Key, "-"), studentId,
                student.StudentNo, student.Name, student.ClassName, student.Phone,
                group.Select(x => (x.EntitlementDate, x.Quantity, x.ConsumedQuantity)), today))
            .OrderBy(x => x.MealName, StringComparer.CurrentCulture)];
    }

    /// <summary>
    /// Hakedisi bitmek uzere olan (ve BITMIS) ogrenciler. Suresi gecmis olanlar esikten
    /// bagimsiz her zaman girer: bittigini gec fark etmek de ayni derttir.
    /// </summary>
    public async Task<IReadOnlyList<EntitlementPeriodSummary>> ExpiringAsync(ExpiringEntitlementQuery query,
        DateOnly today, CancellationToken cancellationToken)
    {
        var rights = dbContext.MealEntitlements.AsNoTracking().Where(x => x.Status == "Active");
        if (query.MealTypeId.HasValue) rights = rights.Where(x => x.MealTypeId == query.MealTypeId);
        if (!string.IsNullOrWhiteSpace(query.ClassKind))
        {
            var preschoolOnly = query.ClassKind == ClassKinds.Preschool;
            rights = rights.Where(x => preschoolOnly
                ? dbContext.Students.Any(s => s.Id == x.StudentId
                    && dbContext.Set<SchoolClass>().Any(c => c.Id == s.ClassId && c.Kind == ClassKinds.Preschool))
                : !dbContext.Students.Any(s => s.Id == x.StudentId
                    && dbContext.Set<SchoolClass>().Any(c => c.Id == s.ClassId && c.Kind == ClassKinds.Preschool)));
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var raw = query.Search.Trim();
            var like = $"%{raw}%";
            var normalized = $"%{TurkishSearchText.Normalize(raw)}%";
            rights = rights.Where(x => dbContext.Students.Any(s => s.Id == x.StudentId
                && (EF.Functions.Like(s.SearchName, normalized)
                    || EF.Functions.Like(s.StudentNo, like)
                    || dbContext.Set<SchoolClass>().Any(c => c.Id == s.ClassId
                        && EF.Functions.Like(c.SearchName, normalized)))));
        }

        // Yalnizca AKTIF ogrenciler: pasife alinmis ogrencinin yenilenecek hakki yoktur.
        rights = rights.Where(x => dbContext.Students.Any(s => s.Id == x.StudentId && s.IsActive));

        // Ozet TAMAMEN VERITABANINDA hesaplanir: hangi ciftin ilgili oldugu, ilk/son gun
        // ve toplamlar tek GROUP BY ile gelir. Once TUM aktif haklar hafizaya cekiliyordu
        // (olculdu: 400 ogrenci x 180 gun icin 72.401 satir, 394 ms, sonuc 0 satir).
        //
        // "Bitisi yaklasan" = o ciftin EN SON hak gunu esikten kucuk/esit. Suresi GECMIS
        // olanlar da bu kosula dogal olarak girer (son gun bugunden de kucuktur).
        //
        // DateOnly SQLite'ta ISO-8601 metindir (yyyy-MM-dd): metin siralamasi tarih
        // siralamasiyla AYNIDIR, bu yuzden MIN/MAX dogru sonuc verir.
        var threshold = today.AddDays(Math.Max(0, query.WithinDays));
        var groups = await rights
            .GroupBy(x => new { x.StudentId, x.MealTypeId })
            .Where(g => g.Max(x => x.EntitlementDate) <= threshold)
            .Select(g => new
            {
                g.Key.StudentId,
                g.Key.MealTypeId,
                FirstDate = g.Min(x => x.EntitlementDate),
                LastDate = g.Max(x => x.EntitlementDate),
                Total = g.Sum(x => x.Quantity),
                Consumed = g.Sum(x => x.ConsumedQuantity),
                // "Kalan" YALNIZCA bugun ve sonrasidir; gecmiste kullanilmayan hak yanmistir.
                Remaining = g.Sum(x => x.EntitlementDate >= today ? x.Quantity - x.ConsumedQuantity : 0),
                Expired = g.Sum(x => x.EntitlementDate < today ? x.Quantity - x.ConsumedQuantity : 0)
            })
            .ToListAsync(cancellationToken);
        if (groups.Count == 0) return [];

        var studentIds = groups.Select(x => x.StudentId).Distinct().ToArray();
        var students = await dbContext.Students.AsNoTracking().Where(x => studentIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                x.StudentNo,
                Name = x.FirstName + " " + x.LastName,
                ClassName = dbContext.Set<SchoolClass>().Where(c => c.Id == x.ClassId).Select(c => c.Name).FirstOrDefault(),
                Phone = dbContext.Set<Parent>().Where(p => p.StudentId == x.Id && p.IsActive)
                    .OrderByDescending(p => p.IsPrimary).Select(p => p.NormalizedPhone).FirstOrDefault()
            })
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var mealNames = await MealNamesAsync(groups.Select(x => x.MealTypeId), cancellationToken);

        // Hicbir hakki kalmamis donem listelenmez: yenilenecek bir sey yoktur. Suresi
        // GECMIS donemler kalan 0 olsa da kalir -- gec fark edilen bitis de bir derttir.
        return [.. groups
            .Select(group =>
            {
                var owner = students.GetValueOrDefault(group.StudentId);
                return new EntitlementPeriodSummary(group.StudentId, owner?.StudentNo ?? "", owner?.Name ?? "",
                    owner?.ClassName, owner?.Phone, group.MealTypeId,
                    mealNames.GetValueOrDefault(group.MealTypeId, "-"),
                    RemainingQuantity: group.Remaining, ExpiredQuantity: group.Expired,
                    TotalQuantity: group.Total, ConsumedQuantity: group.Consumed,
                    FirstDate: group.FirstDate, LastDate: group.LastDate,
                    RenewFrom: group.LastDate.AddDays(1),
                    DaysLeft: group.LastDate.DayNumber - today.DayNumber);
            })
            .Where(x => x.RemainingQuantity > 0 || x.IsExpired)
            .OrderBy(x => x.DaysLeft).ThenBy(x => x.StudentName, StringComparer.CurrentCulture)];
    }

    private async Task<Dictionary<Guid, string>> MealNamesAsync(IEnumerable<Guid> mealTypeIds,
        CancellationToken cancellationToken)
    {
        var ids = mealTypeIds.Distinct().ToArray();
        return await dbContext.Set<MealType>().AsNoTracking().Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
    }

    /// <summary>
    /// Bir ogunun hak satirlarini tek donem ozetine indirger.
    ///
    /// "Kalan" YALNIZCA bugun ve sonrasidir: gecmiste kullanilmayan hak yanmistir ve
    /// veliye "kalan" diye soylenirse yanlis olur. Yanan miktar ayrica ExpiredQuantity
    /// olarak tasinir ki kullanici hakkin bosa gittigini de gorebilsin.
    /// </summary>
    private static EntitlementPeriodSummary Summarize(Guid mealTypeId, string mealName, Guid studentId,
        string studentNo, string studentName, string? className, string? parentPhone,
        IEnumerable<(DateOnly Date, int Quantity, int Consumed)> rows, DateOnly today)
    {
        var days = rows.ToList();
        var firstDate = days.Min(x => x.Date);
        var lastDate = days.Max(x => x.Date);
        return new EntitlementPeriodSummary(studentId, studentNo, studentName, className, parentPhone,
            mealTypeId, mealName,
            RemainingQuantity: days.Where(x => x.Date >= today).Sum(x => x.Quantity - x.Consumed),
            ExpiredQuantity: days.Where(x => x.Date < today).Sum(x => x.Quantity - x.Consumed),
            TotalQuantity: days.Sum(x => x.Quantity),
            ConsumedQuantity: days.Sum(x => x.Consumed),
            FirstDate: firstDate,
            LastDate: lastDate,
            RenewFrom: lastDate.AddDays(1),
            DaysLeft: lastDate.DayNumber - today.DayNumber);
    }

    /// <summary>
    /// Hakki turnikeden GECMEDEN duser (elle kullanim ucu).
    ///
    /// <para>
    /// Turnike yolu (EfAccessDecisionRepository.TryConsumeAndLogAsync) AccessLog +
    /// MealUsage yazar; bu yol yazmaz, cunku ortada bir gecis yoktur. Ama IZSIZ de
    /// kalmaz: denetim kaydi birakilir. Once hicbir iz yoktu ve veli "hakkim 20'ydi,
    /// 18 kaldi, nerede kullanildi?" diye sordugunda CEVAP VERILEMIYORDU.
    /// </para>
    /// </summary>
    public async Task<bool> TryConsumeAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        var changed = await dbContext.MealEntitlements.Where(x => x.Id == entitlementId && x.Status == "Active" && x.ConsumedQuantity < x.Quantity)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ConsumedQuantity, x => x.ConsumedQuantity + 1)
                .SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken) == 1;
        if (!changed) return false;

        // Hangi hakkin elle dusuruldugu KAYDA GECER; aksi halde hak sessizce eksilir.
        var row = await dbContext.MealEntitlements.AsNoTracking()
            .Where(x => x.Id == entitlementId)
            .Select(x => new { x.StudentId, x.EntitlementDate, x.MealTypeId })
            .SingleOrDefaultAsync(cancellationToken);
        auditService.Record(new AuditEntry("EntitlementConsumedManually", nameof(MealEntitlement),
            entitlementId.ToString(),
            row is null
                ? "Yemek hakkı elle düşüldü (turnike geçişi olmadan)."
                : string.Create(CultureInfo.GetCultureInfo("tr-TR"),
                    $"Yemek hakkı elle düşüldü (turnike geçişi olmadan): {row.EntitlementDate:dd.MM.yyyy}."),
            1));
        await dbContext.SaveChangesAsync(cancellationToken);
        accessCache?.Publish(new(ClearAll: true));
        return true;
    }

    public async Task<bool> CancelAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        try { return (await CancelBulkAsync([entitlementId], 1, cancellationToken)).CancelledCount == 1; }
        catch (EntityConflictException) { return false; }
    }

    public Task<CancelEntitlementsResult> CancelBulkAsync(IReadOnlyCollection<Guid> entitlementIds,
        int expectedAffectedCount, CancellationToken cancellationToken) =>
        CancelBulkAsync(entitlementIds, expectedAffectedCount, null, cancellationToken);

    /// <param name="withinTransaction">
    /// Iptal COMMIT EDILMEDEN once, AYNI transaction icinde calisacak is (iade). Hata
    /// atarsa iptal de geri alinir.
    ///
    /// <para>
    /// Once iptal ve iade ayri transaction'lardaydi: iptal commit ediliyor, iade ayri
    /// yaziliyordu. Iade adimi cokerse hak iptal edilmis ama para kasada kalmis oluyordu
    /// -- ve DUZELTILEMIYORDU, cunku ikinci denemede haklar zaten "Cancelled" oldugu icin
    /// bu metot EntityConflictException firlatiyordu.
    /// </para>
    /// </param>
    public async Task<CancelEntitlementsResult> CancelBulkAsync(IReadOnlyCollection<Guid> entitlementIds,
        int expectedAffectedCount, Func<CancellationToken, Task>? withinTransaction,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var items = await dbContext.MealEntitlements.AsNoTracking().Where(x => entitlementIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (items.Count != expectedAffectedCount || items.Any(x => x.Status != "Active" || x.ConsumedQuantity != 0))
            throw new EntityConflictException("Seçim değişti veya kullanılan/iptal edilmiş hak içeriyor. Listeyi yenileyin.");
        var operationId = Guid.NewGuid();
        var changed = await dbContext.MealEntitlements.Where(x => entitlementIds.Contains(x.Id) && x.Status == "Active" && x.ConsumedQuantity == 0)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Status, "Cancelled")
                .SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken);
        if (changed != expectedAffectedCount) throw new EntityConflictException("Seçim işlem sırasında değişti. Listeyi yenileyin.");
        auditService.Record(new AuditEntry("EntitlementsCancelled", nameof(MealEntitlement), operationId.ToString(),
            "Seçili yemek hakları iptal edildi.", changed, After: new { Count = changed }, BulkOperationId: operationId));
        await dbContext.SaveChangesAsync(cancellationToken);
        // Iade AYNI transaction icinde: hata atarsa asagidaki commit hic calismaz ve
        // iptal de geri alinir. Boylece operator ayni islemi sorunsuz tekrarlayabilir.
        if (withinTransaction is not null) await withinTransaction(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var studentId in items.Select(x => x.StudentId).Distinct())
            accessCache?.Publish(new(StudentId: studentId));
        return new CancelEntitlementsResult(changed);
    }

    private async Task<List<MealEntitlement>> LoadExistingAsync(IReadOnlyCollection<Guid> studentIds, Guid mealTypeId,
        IReadOnlyCollection<DateOnly> dates, bool tracked, CancellationToken cancellationToken)
    {
        var result = new List<MealEntitlement>();
        foreach (var chunk in studentIds.Chunk(500))
        {
            var query = dbContext.MealEntitlements.Where(x => chunk.Contains(x.StudentId) && x.MealTypeId == mealTypeId
                && dates.Contains(x.EntitlementDate));
            if (!tracked) query = query.AsNoTracking();
            result.AddRange(await query.ToListAsync(cancellationToken));
        }
        return result;
    }

    private static string StateHash(IEnumerable<MealEntitlement> rows)
    {
        var value = string.Join('|', rows.OrderBy(x => x.StudentId).ThenBy(x => x.EntitlementDate)
            .Select(x => $"{x.Id:N}:{x.StudentId:N}:{x.EntitlementDate:yyyyMMdd}:{x.Quantity}:{x.ConsumedQuantity}:{x.Status}:{x.Version}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

}
