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
            }
        }
        var operationId = Guid.NewGuid();
        auditService.Record(new AuditEntry("EntitlementsGranted", nameof(MealEntitlement), operationId.ToString(),
            "Toplu yemek hakkı tanımlandı.", created + updated,
            After: new { StudentCount = studentIds.Count, DateCount = dates.Count, mealTypeId, quantity, source, Created = created, Updated = updated },
            BulkOperationId: operationId));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BulkEntitlementResult(studentIds.Count, dates.Count, created, updated);
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

    public async Task<bool> TryConsumeAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        var changed = await dbContext.MealEntitlements.Where(x => x.Id == entitlementId && x.Status == "Active" && x.ConsumedQuantity < x.Quantity)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.ConsumedQuantity, x => x.ConsumedQuantity + 1)
                .SetProperty(x => x.Version, x => x.Version + 1).SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), cancellationToken) == 1;
        if (changed) accessCache?.Publish(new(ClearAll: true));
        return changed;
    }

    public async Task<bool> CancelAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        try { return (await CancelBulkAsync([entitlementId], 1, cancellationToken)).CancelledCount == 1; }
        catch (EntityConflictException) { return false; }
    }

    public async Task<CancelEntitlementsResult> CancelBulkAsync(IReadOnlyCollection<Guid> entitlementIds,
        int expectedAffectedCount, CancellationToken cancellationToken)
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
