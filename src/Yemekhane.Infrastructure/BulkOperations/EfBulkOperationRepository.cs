using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.BulkOperations;

public sealed class EfBulkOperationRepository(YemekhaneDbContext db, IAuditService audit, TimeProvider timeProvider)
    : IBulkOperationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<BulkOperationState> PreviewAsync(BulkCalendarOperationRequest request, CancellationToken cancellationToken)
    {
        var dates = BulkOperationService.Dates(request);
        var studentIds = await ResolveStudentsAsync(request.Scope, cancellationToken);
        var rights = new List<MealEntitlement>();
        foreach (var chunk in studentIds.Chunk(400))
        {
            var query = db.MealEntitlements.AsNoTracking().Where(x => chunk.Contains(x.StudentId)
                && dates.Contains(x.EntitlementDate) && x.Status == "Active");
            if (request.MealTypeId.HasValue) query = query.Where(x => x.MealTypeId == request.MealTypeId);
            rights.AddRange(await query.ToListAsync(cancellationToken));
        }
        var affected = rights.Where(x => x.Quantity > x.ConsumedQuantity).ToArray();
        // Onizleme tablosu ogrenciyi GUID ile degil no + ad + sinif ile gostersin diye
        // etkilenen ogrencilerin kimligi tek sorguyla cekilir (ayni adli ogrenciler
        // ancak numara ve sinifla ayirt edilebilir). Durum ozetine (StateHash) girmez.
        var affectedStudentIds = affected.Select(x => x.StudentId).Distinct().ToArray();
        var identity = new Dictionary<Guid, (string No, string Name, string? Class)>();
        foreach (var chunk in affectedStudentIds.Chunk(400))
        {
            var found = await db.Students.AsNoTracking().Where(s => chunk.Contains(s.Id))
                .Select(s => new { s.Id, s.StudentNo, s.FirstName, s.LastName,
                    ClassName = db.Set<SchoolClass>().Where(c => c.Id == s.ClassId).Select(c => c.Name).FirstOrDefault() })
                .ToListAsync(cancellationToken);
            foreach (var s in found) identity[s.Id] = (s.StudentNo, s.FirstName + " " + s.LastName, s.ClassName);
        }
        var rows = affected.OrderBy(x => x.StudentId).ThenBy(x => x.EntitlementDate).ThenBy(x => x.MealTypeId)
            .Select(x =>
            {
                identity.TryGetValue(x.StudentId, out var who);
                return new BulkAffectedEntitlement(x.Id, x.StudentId, x.MealTypeId, x.EntitlementDate,
                    x.Quantity, x.ConsumedQuantity, x.Quantity - x.ConsumedQuantity, x.Version, null,
                    who.No ?? "", who.Name ?? "", who.Class);
            }).ToArray();
        var hashRows = rights.OrderBy(x => x.StudentId).ThenBy(x => x.EntitlementDate).ThenBy(x => x.MealTypeId)
            .Select(x => new BulkAffectedEntitlement(x.Id, x.StudentId, x.MealTypeId, x.EntitlementDate,
                x.Quantity, x.ConsumedQuantity, Math.Max(0, x.Quantity - x.ConsumedQuantity), x.Version, null)).ToArray();
        return new BulkOperationState(studentIds, rows, rights.Count(x => x.ConsumedQuantity > 0),
            await StateHashAsync(studentIds, hashRows, dates, cancellationToken));
    }

    public async Task<BulkOperationResult?> FindIdempotentAsync(string idempotencyKey, string requestHash, CancellationToken cancellationToken)
    {
        var operation = await db.BulkOperations.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (operation is null) return null;
        if (operation.RequestHash != requestHash) throw new EntityConflictException("IdempotencyKey farklı bir istek için daha önce kullanılmış.");
        return JsonSerializer.Deserialize<BulkOperationResult>(operation.ResultJson, JsonOptions)
            ?? throw new InvalidOperationException("Toplu işlem sonucu okunamadı.");
    }

    public Task<BulkOperationResult> ApplyAsync(BulkCalendarOperationRequest request, string requestHash,
        string expectedStateHash, IReadOnlyDictionary<Guid, DateOnly> targetDates, Guid createdBy,
        CancellationToken cancellationToken) => SqliteBusyRetry.ExecuteAsync(
            () => ApplyCoreAsync(request, requestHash, expectedStateHash, targetDates, createdBy, cancellationToken),
            db.ChangeTracker.Clear, cancellationToken);

    private async Task<BulkOperationResult> ApplyCoreAsync(BulkCalendarOperationRequest request, string requestHash,
        string expectedStateHash, IReadOnlyDictionary<Guid, DateOnly> targetDates, Guid createdBy,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var duplicate = await FindIdempotentAsync(request.IdempotencyKey.Trim(), requestHash, cancellationToken);
        if (duplicate is not null) { await transaction.CommitAsync(cancellationToken); return duplicate with { IdempotentReplay = true }; }

        var current = await PreviewAsync(request, cancellationToken);
        if (!FixedEquals(expectedStateHash, current.StateHash))
            throw new EntityConflictException("Önizlemeden sonra kapsam, takvim veya hakediş verisi değişti. İşlem uygulanmadı.");

        var now = timeProvider.GetUtcNow();
        var operation = new BulkOperation
        {
            Id = Guid.NewGuid(), IdempotencyKey = request.IdempotencyKey.Trim(), RequestHash = requestHash,
            OperationType = request.Operation, Status = "Completed", RequestJson = JsonSerializer.Serialize(request, JsonOptions),
            ResultJson = "{}", CreatedBy = createdBy, CreatedAt = now
        };
        db.BulkOperations.Add(operation);
        var undoRights = new List<UndoEntitlement>();
        var createdTargets = new HashSet<Guid>();
        var targetUndo = new Dictionary<Guid, UndoEntitlement>();
        var transferIds = new List<Guid>();
        var eventRefs = new List<UndoEntity>();
        var sourceIds = current.Entitlements.Select(x => x.EntitlementId).ToArray();
        var sources = await db.MealEntitlements.Where(x => sourceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var transfer = request.TransferBehavior is "NextBusinessDay" or "SpecifiedDate";
        var newStatus = transfer ? "Transferred" : request.TransferBehavior == "Forfeit" ? "Forfeited" : "Cancelled";
        var targetDateValues = targetDates.Values.Distinct().ToArray();
        var affectedStudents = current.Entitlements.Select(x => x.StudentId).Distinct().ToArray();
        var affectedMeals = current.Entitlements.Select(x => x.MealTypeId).Distinct().ToArray();
        var existingTargets = transfer
            ? await db.MealEntitlements.Where(x => affectedStudents.Contains(x.StudentId) && affectedMeals.Contains(x.MealTypeId)
                && targetDateValues.Contains(x.EntitlementDate)).ToListAsync(cancellationToken)
            : [];
        var targetMap = existingTargets.ToDictionary(x => (x.StudentId, x.MealTypeId, x.EntitlementDate));

        foreach (var preview in current.Entitlements)
        {
            var source = sources[preview.EntitlementId];
            undoRights.Add(Snapshot(source, source.Version + 1));
            source.Status = newStatus; source.Version++; source.UpdatedAt = now;
            if (!transfer) continue;

            var targetDate = targetDates[preview.EntitlementId];
            var targetKey = (source.StudentId, source.MealTypeId, targetDate);
            if (!targetMap.TryGetValue(targetKey, out var target))
            {
                target = new MealEntitlement { Id = Guid.NewGuid(), StudentId = source.StudentId, MealTypeId = source.MealTypeId,
                    EntitlementDate = targetDate, Quantity = preview.AffectedQuantity, ConsumedQuantity = 0,
                    Status = "Active", Source = "BulkTransfer", Version = 0, CreatedAt = now };
                db.MealEntitlements.Add(target); createdTargets.Add(target.Id); targetMap[targetKey] = target;
            }
            else
            {
                if (!createdTargets.Contains(target.Id))
                {
                    if (!targetUndo.ContainsKey(target.Id)) targetUndo[target.Id] = Snapshot(target, 0);
                    target.Version++; target.UpdatedAt = now;
                }
                target.Quantity += preview.AffectedQuantity; target.Status = "Active";
            }
            var transferRow = new MealTransfer { Id = Guid.NewGuid(), StudentId = source.StudentId, MealTypeId = source.MealTypeId,
                SourceEntitlementId = source.Id, OriginalDate = source.EntitlementDate, TargetDate = targetDate,
                Quantity = preview.AffectedQuantity, Reason = request.Description?.Trim() ?? request.Operation,
                CreatedBy = createdBy, BulkOperationId = operation.Id, CreatedAt = now };
            db.MealTransfers.Add(transferRow); transferIds.Add(transferRow.Id);
        }

        foreach (var item in targetUndo.Values)
        {
            var target = db.ChangeTracker.Entries<MealEntitlement>().Single(x => x.Entity.Id == item.Id).Entity;
            item.ExpectedVersion = target.Version;
        }
        CreateEvents(request, current.ScopeStudentIds, operation.Id, createdBy, now, eventRefs);

        var quantity = current.Entitlements.Sum(x => x.AffectedQuantity);
        var result = new BulkOperationResult(operation.Id, "Completed",
            current.ScopeStudentIds.Count, current.Entitlements.Count, quantity,
            transfer ? 0 : quantity, transfer ? quantity : 0, targetDates.Values.Distinct().Order().ToArray());
        var undo = new UndoPayload(undoRights, targetUndo.Values.ToArray(), createdTargets.ToArray(), transferIds, eventRefs);
        operation.UndoJson = JsonSerializer.Serialize(undo, JsonOptions);
        operation.ResultJson = JsonSerializer.Serialize(result, JsonOptions);
        audit.Record(new AuditEntry("BulkOperationApplied", nameof(BulkOperation), operation.Id.ToString(),
            $"{request.Operation} toplu takvim işlemi uygulandı.", current.Entitlements.Count,
            After: result, BulkOperationId: operation.Id, UserId: createdBy));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var replay = await FindIdempotentAsync(request.IdempotencyKey.Trim(), requestHash, cancellationToken);
            if (replay is not null) return replay with { IdempotentReplay = true };
            throw;
        }
    }

    public async Task<BulkOperationHistoryPage> HistoryAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.BulkOperations.AsNoTracking();
        var total = await query.CountAsync(cancellationToken);
        // SQLite DateTimeOffset'i ORDER BY'da desteklemez (NotSupportedException -> 500):
        // "Gecmis" dugmesi ve uygulama sonrasi gecmis yenileme her seferinde patliyor,
        // sihirbaz da bunu "uygulanamadi" diye gosteriyordu. Projenin yerlesik cozumu
        // JulianDay sayisal cevirisiyle siralamaktir (bkz. SettingsService, EfReportRepository).
        var entities = await query.OrderByDescending(x => YemekhaneDbContext.JulianDay(x.CreatedAt)).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = entities.Select(x =>
        {
            var result = JsonSerializer.Deserialize<BulkOperationResult>(x.ResultJson, JsonOptions)!;
            return new BulkOperationHistoryItem(x.Id, x.OperationType, x.Status, x.CreatedAt, x.CreatedBy,
                result.StudentCount, result.EntitlementCount, result.Quantity, x.Status == "Completed" && x.RevertedAt is null, x.RevertedAt);
        }).ToArray();
        return new BulkOperationHistoryPage(items, page, pageSize, total);
    }

    public async Task<UndoBulkOperationResult> UndoAsync(Guid operationId, Guid revertedBy, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var operation = await db.BulkOperations.SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken)
            ?? throw new EntityNotFoundException("Toplu işlem bulunamadı.");
        if (operation.RevertedAt.HasValue || operation.Status != "Completed")
            return new UndoBulkOperationResult(operationId, true, "İşlem daha önce geri alındı.");
        var undo = JsonSerializer.Deserialize<UndoPayload>(operation.UndoJson ?? "", JsonOptions)
            ?? throw new EntityConflictException("Geri alma verisi bulunamadı.");

        var allIds = undo.Sources.Select(x => x.Id).Concat(undo.Targets.Select(x => x.Id)).Concat(undo.CreatedTargetIds).Distinct().ToArray();
        var current = await db.MealEntitlements.Where(x => allIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (undo.Sources.Any(x => !current.TryGetValue(x.Id, out var row) || row.Version != x.ExpectedVersion || row.ConsumedQuantity > 0)
            || undo.Targets.Any(x => !current.TryGetValue(x.Id, out var row) || row.Version != x.ExpectedVersion || row.ConsumedQuantity > 0)
            || undo.CreatedTargetIds.Any(id => !current.TryGetValue(id, out var row) || row.Version != 0 || row.ConsumedQuantity > 0))
            throw new EntityConflictException("Geri alma yapılamadı: işlem kayıtları değişmiş veya hak kullanılmış. Hiçbir kayıt değiştirilmedi.");

        foreach (var snapshot in undo.Sources.Concat(undo.Targets)) Restore(current[snapshot.Id], snapshot);
        foreach (var id in undo.CreatedTargetIds) db.MealEntitlements.Remove(current[id]);
        var transfers = await db.MealTransfers.Where(x => undo.TransferIds.Contains(x.Id) && x.BulkOperationId == operationId).ToListAsync(cancellationToken);
        if (transfers.Count != undo.TransferIds.Count)
            throw new EntityConflictException("Geri alma yapılamadı: aktarım kayıtları değişmiş. Hiçbir kayıt değiştirilmedi.");
        db.MealTransfers.RemoveRange(transfers);
        await RemoveEventsAsync(undo.Events, cancellationToken);
        operation.Status = "Reverted"; operation.RevertedAt = timeProvider.GetUtcNow(); operation.UpdatedAt = operation.RevertedAt;
        audit.Record(new AuditEntry("BulkOperationUndone", nameof(BulkOperation), operation.Id.ToString(),
            "Toplu takvim işlemi geri alındı.", undo.Sources.Count, BulkOperationId: operation.Id, UserId: revertedBy));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UndoBulkOperationResult(operationId, true, "Toplu işlem geri alındı.");
    }

    private async Task<IReadOnlyList<Guid>> ResolveStudentsAsync(BulkOperationScope scope, CancellationToken cancellationToken)
    {
        var query = db.Students.AsNoTracking().Where(x => x.IsActive);
        if (scope.Type == "Manual")
        {
            var ids = (scope.StudentIds ?? Array.Empty<Guid>()).Distinct().ToArray();
            var studentNos = (scope.StudentNos ?? []).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            if (studentNos.Length > 0)
            {
                // Numara ile verilen ogrenci bulunamazsa istek reddedilir; sessiz atlama
                // kullanicinin "N ogrenci" beklentisini bozar ve fark edilmez.
                var found = await db.Students.AsNoTracking().Where(x => x.IsActive && studentNos.Contains(x.StudentNo))
                    .Select(x => new { x.Id, x.StudentNo }).ToListAsync(cancellationToken);
                var missing = studentNos.Except(found.Select(x => x.StudentNo), StringComparer.Ordinal).ToArray();
                if (missing.Length > 0) throw new RequestValidationException($"Aktif öğrenci bulunamadı: {string.Join(", ", missing)}");
                ids = ids.Concat(found.Select(x => x.Id)).Distinct().ToArray();
            }
            return await query.Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(cancellationToken);
        }
        query = scope.Type switch
        {
            "AllSchool" => query,
            "Class" => query.Where(x => x.ClassId == scope.ScopeId),
            "Group" => query.Where(x => db.Set<StudentGroupMember>().Any(m => m.GroupId == scope.ScopeId && m.StudentId == x.Id)),
            _ => throw new RequestValidationException("Kapsam türü geçersiz.")
        };
        return await query.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(cancellationToken);
    }

    private async Task<string> StateHashAsync(IReadOnlyList<Guid> students, IReadOnlyList<BulkAffectedEntitlement> rights,
        IReadOnlyList<DateOnly> dates, CancellationToken cancellationToken)
    {
        var holidays = await db.Holidays.AsNoTracking().Where(x => dates.Contains(x.Date))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Date, x.UpdatedAt }).ToArrayAsync(cancellationToken);
        var exceptions = await db.Set<ScheduleOverride>().AsNoTracking().Where(x => dates.Contains(x.Date))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Date, x.UpdatedAt }).ToArrayAsync(cancellationToken);
        var value = JsonSerializer.Serialize(new { students, rights, holidays, exceptions }, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private void CreateEvents(BulkCalendarOperationRequest request, IReadOnlyList<Guid> students, Guid operationId,
        Guid createdBy, DateTimeOffset now, List<UndoEntity> events)
    {
        var dates = BulkOperationService.Dates(request);
        if (request.Operation == "Holiday")
        {
            foreach (var date in dates)
            {
                var holiday = new Holiday { Id = Guid.NewGuid(), Date = date, Name = request.Description?.Trim() ?? "Toplu tatil",
                    HolidayType = "Bulk", TransferBehavior = request.TransferBehavior, CreatedAt = now };
                db.Holidays.Add(holiday); events.Add(new("Holiday", holiday.Id));
                var scope = new HolidayScope { Id = Guid.NewGuid(), HolidayId = holiday.Id, ScopeType = request.Scope.Type,
                    ScopeId = request.Scope.ScopeId, CreatedAt = now };
                db.Set<HolidayScope>().Add(scope); events.Add(new("HolidayScope", scope.Id));
            }
        }
        else if (request.Operation == "Trip")
        {
            foreach (var date in dates)
            {
                var item = new ScheduleOverride { Id = Guid.NewGuid(), Date = date, ExceptionType = "Trip",
                    ScopeType = request.Scope.Type, ScopeId = request.Scope.ScopeId, MealTypeId = request.MealTypeId,
                    EntitlementBehavior = request.TransferBehavior, TargetDate = request.TargetDate,
                    Description = request.Description, CreatedBy = createdBy, CreatedAt = now };
                db.Set<ScheduleOverride>().Add(item); events.Add(new("ScheduleOverride", item.Id));
            }
        }
        else if (request.Operation == "Leave")
        {
            foreach (var studentId in students)
            foreach (var date in dates)
            {
                var leave = new StudentLeave { Id = Guid.NewGuid(), StudentId = studentId, StartsOn = date, EndsOn = date,
                    LeaveType = "Bulk", Description = request.Description, EntitlementBehavior = request.TransferBehavior, CreatedAt = now };
                db.Set<StudentLeave>().Add(leave); events.Add(new("StudentLeave", leave.Id));
            }
        }
    }

    private async Task RemoveEventsAsync(IReadOnlyList<UndoEntity> events, CancellationToken cancellationToken)
    {
        foreach (var group in events.GroupBy(x => x.Type))
        {
            var ids = group.Select(x => x.Id).ToArray();
            var removed = 0;
            switch (group.Key)
            {
                case "HolidayScope": var scopes = await db.Set<HolidayScope>().Where(x => ids.Contains(x.Id) && x.UpdatedAt == null).ToListAsync(cancellationToken); removed = scopes.Count; db.RemoveRange(scopes); break;
                case "Holiday": var holidays = await db.Holidays.Where(x => ids.Contains(x.Id) && x.UpdatedAt == null).ToListAsync(cancellationToken); removed = holidays.Count; db.RemoveRange(holidays); break;
                case "ScheduleOverride": var overrides = await db.Set<ScheduleOverride>().Where(x => ids.Contains(x.Id) && x.UpdatedAt == null).ToListAsync(cancellationToken); removed = overrides.Count; db.RemoveRange(overrides); break;
                case "StudentLeave": var leaves = await db.Set<StudentLeave>().Where(x => ids.Contains(x.Id) && x.UpdatedAt == null).ToListAsync(cancellationToken); removed = leaves.Count; db.RemoveRange(leaves); break;
            }
            if (removed != ids.Length)
                throw new EntityConflictException("Geri alma yapılamadı: takvim kayıtları değişmiş. Hiçbir kayıt değiştirilmedi.");
        }
    }

    private static UndoEntitlement Snapshot(MealEntitlement x, long expectedVersion) =>
        new(x.Id, x.Quantity, x.ConsumedQuantity, x.Status, x.Source, x.Version, expectedVersion);
    private static void Restore(MealEntitlement row, UndoEntitlement value)
    {
        row.Quantity = value.Quantity; row.ConsumedQuantity = value.ConsumedQuantity; row.Status = value.Status;
        row.Source = value.Source; row.Version++; row.UpdatedAt = DateTimeOffset.UtcNow;
    }
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private sealed record UndoPayload(IReadOnlyList<UndoEntitlement> Sources, IReadOnlyList<UndoEntitlement> Targets,
        IReadOnlyList<Guid> CreatedTargetIds, IReadOnlyList<Guid> TransferIds, IReadOnlyList<UndoEntity> Events);
    private sealed class UndoEntitlement(Guid id, int quantity, int consumedQuantity, string status, string? source,
        long version, long expectedVersion)
    {
        public Guid Id { get; } = id;
        public int Quantity { get; } = quantity;
        public int ConsumedQuantity { get; } = consumedQuantity;
        public string Status { get; } = status;
        public string? Source { get; } = source;
        public long Version { get; } = version;
        public long ExpectedVersion { get; set; } = expectedVersion;
    }
    private sealed record UndoEntity(string Type, Guid Id);
}
