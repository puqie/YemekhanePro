using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Sms;

/// <summary>
/// Otomatik SMS kurallari <c>system_settings</c> tablosunda JSON olarak durur; sema degismez.
/// Anahtarlar: <c>sms.automation</c> (kurallar), <c>sms.automation.lastRunDate</c> (zamanlanmis
/// hak uyarisinin son kostugu Istanbul gunu, yyyy-MM-dd).
/// </summary>
public sealed class EfSmsAutomationStore(YemekhaneDbContext db, TimeProvider timeProvider) : ISmsAutomationStore
{
    public const string SettingsKey = "sms.automation";
    public const string LastRunKey = "sms.automation.lastRunDate";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SmsAutomationSettings?> GetAsync(CancellationToken cancellationToken)
    {
        var row = await db.Set<SystemSetting>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Key == SettingsKey && !x.IsSecret, cancellationToken);
        if (row is null || string.IsNullOrWhiteSpace(row.Value)) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<SmsAutomationSettings>(row.Value, Json);
            // Eski/bozuk JSON'da eksik kural: varsayilanla tamamla ki ekran ve isleyici cokmesin.
            if (parsed is null) return null;
            var fallback = SmsAutomationSettings.Default;
            return new SmsAutomationSettings(parsed.EntitlementWarning ?? fallback.EntitlementWarning,
                parsed.IncomeNotice ?? fallback.IncomeNotice, parsed.CardReplacement ?? fallback.CardReplacement);
        }
        catch (JsonException) { return null; }
    }

    public Task SaveAsync(SmsAutomationSettings settings, CancellationToken cancellationToken) =>
        UpsertAsync(SettingsKey, JsonSerializer.Serialize(settings, Json), cancellationToken);

    public async Task<DateOnly?> GetLastRunDateAsync(CancellationToken cancellationToken)
    {
        var value = await db.Set<SystemSetting>().AsNoTracking().Where(x => x.Key == LastRunKey)
            .Select(x => x.Value).SingleOrDefaultAsync(cancellationToken);
        return DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;
    }

    public Task SetLastRunDateAsync(DateOnly runDate, CancellationToken cancellationToken) =>
        UpsertAsync(LastRunKey, runDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), cancellationToken);

    private async Task UpsertAsync(string key, string value, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var row = await db.Set<SystemSetting>().SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (row is null)
            db.Add(new SystemSetting { Id = Guid.NewGuid(), Key = key, Value = value, IsSecret = false, CreatedAt = now });
        else { row.Value = value; row.IsSecret = false; row.UpdatedAt = now; }
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class EfSmsAutomationRepository(YemekhaneDbContext db) : ISmsAutomationRepository
{
    public async Task<StudentSmsContact?> GetStudentContactAsync(Guid studentId, CancellationToken cancellationToken)
    {
        var student = await db.Students.AsNoTracking().Where(x => x.Id == studentId)
            .Select(x => new { x.Id, x.StudentNo, x.FirstName, x.LastName, x.ClassId, x.SectionId })
            .SingleOrDefaultAsync(cancellationToken);
        if (student is null) return null;
        var parent = await PrimaryParentsAsync([student.Id], cancellationToken);
        var className = student.ClassId is { } classId
            ? await db.Set<SchoolClass>().AsNoTracking().Where(c => c.Id == classId).Select(c => c.Name).SingleOrDefaultAsync(cancellationToken) : null;
        var sectionName = student.SectionId is { } sectionId
            ? await db.Set<Section>().AsNoTracking().Where(s => s.Id == sectionId).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken) : null;
        parent.TryGetValue(student.Id, out var contact);
        return new StudentSmsContact(student.Id, student.StudentNo, student.FirstName, student.LastName,
            className, sectionName, contact.Name, contact.Phone);
    }

    /// <remarks>
    /// Hesap bellekte yapilir: SQLite'ta alt sorgu icinde <c>Distinct().Count()</c> ve bos kumede
    /// <c>Max(DateOnly)</c> EF cevirisinde kirilgan; gunluk tek kosu icin birkac bin satir okumak ucuz.
    /// </remarks>
    public async Task<IReadOnlyList<EntitlementWarningCandidate>> ListEntitlementWarningCandidatesAsync(
        DateOnly today, int threshold, CancellationToken cancellationToken)
    {
        var students = await db.Students.AsNoTracking().Where(x => x.IsActive)
            .Select(x => new { x.Id, x.StudentNo, x.FirstName, x.LastName, x.ClassId, x.SectionId })
            .ToListAsync(cancellationToken);
        var everHad = (await db.MealEntitlements.AsNoTracking().Select(e => e.StudentId).Distinct().ToListAsync(cancellationToken))
            .ToHashSet();
        var remaining = await db.MealEntitlements.AsNoTracking()
            .Where(e => e.Status == "Active" && e.Quantity > e.ConsumedQuantity && e.EntitlementDate >= today)
            .Select(e => new { e.StudentId, e.EntitlementDate }).Distinct().ToListAsync(cancellationToken);
        var byStudent = remaining.GroupBy(x => x.StudentId)
            .ToDictionary(g => g.Key, g => (Days: g.Select(x => x.EntitlementDate).Distinct().Count(), Last: g.Max(x => x.EntitlementDate)));

        var candidates = students.Where(s => everHad.Contains(s.Id))
            .Select(s => (Student: s, Stats: byStudent.TryGetValue(s.Id, out var stats) ? stats : (Days: 0, Last: (DateOnly?)null)))
            .Where(x => x.Stats.Days <= threshold)
            .OrderBy(x => x.Student.LastName).ThenBy(x => x.Student.FirstName).ToList();
        if (candidates.Count == 0) return [];

        var parents = await PrimaryParentsAsync(candidates.Select(x => x.Student.Id).ToArray(), cancellationToken);
        var classes = await db.Set<SchoolClass>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        var sections = await db.Set<Section>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        return candidates.Select(x =>
        {
            parents.TryGetValue(x.Student.Id, out var parent);
            var contact = new StudentSmsContact(x.Student.Id, x.Student.StudentNo, x.Student.FirstName, x.Student.LastName,
                x.Student.ClassId is { } c && classes.TryGetValue(c, out var cn) ? cn : null,
                x.Student.SectionId is { } s && sections.TryGetValue(s, out var sn) ? sn : null,
                parent.Name, parent.Phone);
            return new EntitlementWarningCandidate(contact, x.Stats.Days, x.Stats.Last);
        }).ToList();
    }

    /// <remarks>
    /// <para>Siralama <c>JulianDay</c> uzerinden: SQLite <c>DateTimeOffset</c> sutununu ORDER BY'da
    /// desteklemez ve duz <c>OrderByDescending(x =&gt; x.ValidTo)</c> calisma aninda
    /// <c>NotSupportedException</c> firlatir (kanca hatayi yutar, SMS sessizce kaybolurdu).</para>
    /// <para><c>CreatedAt</c> son ayirici: ayni anda (ayni saat degeriyle) pasiflesmis birden
    /// fazla kartta <c>ValidTo</c>/<c>ValidFrom</c> esitlenir ve siralama rastgele bir satira
    /// duserdi -- "eski kart no" olarak bir onceki degil, en bastaki kart yaziliyordu.</para>
    /// </remarks>
    public Task<string?> GetReplacedCardNumberAsync(Guid studentId, Guid newCardId, CancellationToken cancellationToken) =>
        db.StudentCards.AsNoTracking()
            .Where(x => x.StudentId == studentId && x.Id != newCardId && !x.IsActive && x.ValidTo != null)
            .OrderByDescending(x => YemekhaneDbContext.JulianDay(x.ValidTo!.Value))
            .ThenByDescending(x => YemekhaneDbContext.JulianDay(x.ValidFrom))
            .ThenByDescending(x => YemekhaneDbContext.JulianDay(x.CreatedAt))
            .Select(x => x.CardNumber).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlySet<string>> ListIdempotencyKeysAsync(string prefix, CancellationToken cancellationToken)
    {
        var keys = await db.SmsLogs.AsNoTracking().Where(x => x.IdempotencyKey.StartsWith(prefix))
            .Select(x => x.IdempotencyKey).ToListAsync(cancellationToken);
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Birincil veli varsa o, yoksa herhangi bir aktif veli (toplu SMS ile ayni kural).</summary>
    private async Task<Dictionary<Guid, (string? Name, string? Phone)>> PrimaryParentsAsync(
        IReadOnlyCollection<Guid> studentIds, CancellationToken cancellationToken)
    {
        var ids = studentIds.ToList();
        var parents = await db.Parents.AsNoTracking()
            .Where(p => p.IsActive && ids.Contains(p.StudentId))
            .Select(p => new { p.StudentId, p.Name, p.NormalizedPhone, p.IsPrimary, p.Id })
            .ToListAsync(cancellationToken);
        return parents.GroupBy(p => p.StudentId).ToDictionary(g => g.Key, g =>
        {
            var first = g.OrderByDescending(p => p.IsPrimary).ThenBy(p => p.Id).First();
            return ((string?)first.Name, (string?)first.NormalizedPhone);
        });
    }
}
