using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Search;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Search;

public sealed class EfGlobalSearchRepository(YemekhaneDbContext db, TimeProvider clock) : IGlobalSearchRepository
{
    public const int GroupLimit = 8;

    public async Task<GlobalSearchResponse> SearchAsync(string query, IReadOnlySet<string> permissions,
        CancellationToken cancellationToken)
    {
        var term = query.Trim();
        if (term.Length == 0 || term.Length > 100) return new(term, []);

        var groups = new List<SearchResultGroup>();
        if (permissions.Contains("students.read"))
        {
            var studentItems = await SearchStudentsAsync(term, cancellationToken);
            Add(groups, "student", "Öğrenciler", studentItems);
            if (term.Length >= 2)
            {
                Add(groups, "class", "Sınıflar", await SearchClassesAsync(term, cancellationToken));
                Add(groups, "group", "Gruplar", await SearchGroupsAsync(term, cancellationToken));
            }
        }

        if (permissions.Contains("calendar.manage") && TurkishDateParser.TryParse(term, Today(), out var date))
            Add(groups, "calendar", "Takvim", await SearchCalendarAsync(date, cancellationToken));
        else if (permissions.Contains("calendar.manage") && term.Length >= 2)
            Add(groups, "calendar", "Takvim", await SearchCalendarEventsAsync(term, cancellationToken));

        if (term.Length >= 2)
            Add(groups, "module", "Modüller", SearchModules(term, permissions));

        return new(term, groups);
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchStudentsAsync(string term, CancellationToken token)
    {
        var general = term.Length >= 2;
        var normalized = TurkishSearchText.Normalize(term);
        var values = await db.Students.AsNoTracking()
            .Where(student => student.StudentNo == term
                || db.StudentCards.Any(card => card.StudentId == student.Id && card.IsActive && card.CardNumber == term)
                || general && (student.SearchName.StartsWith(normalized)
                    || student.SearchName.Contains(" " + normalized)))
            .OrderBy(student => student.StudentNo == term ? 0 : 1)
            .ThenBy(student => student.LastName).ThenBy(student => student.FirstName)
            .Take(GroupLimit)
            .Select(student => new { student.Id, student.FirstName, student.LastName, student.StudentNo })
            .ToListAsync(token);
        return values.Select(student => new SearchResultItem("student", student.FirstName + " " + student.LastName,
            "Öğrenci no: " + student.StudentNo, "student-detail",
            new Dictionary<string, string> { ["id"] = student.Id.ToString() }, "Person")).ToArray();
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchClassesAsync(string term, CancellationToken token)
    {
        var normalized = TurkishSearchText.Normalize(term);
        var values = await db.Set<SchoolClass>().AsNoTracking().Where(value => value.IsActive && value.SearchName.StartsWith(normalized))
            .OrderBy(value => value.Name).Take(GroupLimit)
            .ToListAsync(token);
        return values.Select(value => new SearchResultItem("class", value.Name, "Sınıftaki öğrencileri göster",
            "students", new Dictionary<string, string> { ["classId"] = value.Id.ToString(), ["className"] = value.Name }, "Class")).ToArray();
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchGroupsAsync(string term, CancellationToken token)
    {
        var normalized = TurkishSearchText.Normalize(term);
        var values = await db.Set<StudentGroup>().AsNoTracking().Where(value => value.IsActive && value.SearchName.StartsWith(normalized))
            .OrderBy(value => value.Name).Take(GroupLimit)
            .ToListAsync(token);
        return values.Select(value => new SearchResultItem("group", value.Name, value.GroupType,
            "students", new Dictionary<string, string> { ["groupId"] = value.Id.ToString(), ["groupName"] = value.Name }, "Group")).ToArray();
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchCalendarAsync(DateOnly date, CancellationToken token)
    {
        var results = new List<SearchResultItem>
        {
            new("date", date.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("tr-TR")), "Takvim gününü aç",
                "holiday-transfer", new Dictionary<string, string> { ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, "Calendar")
        };
        var holidays = await db.Holidays.AsNoTracking().Where(value => value.Date == date).OrderBy(value => value.Name)
            .Take(GroupLimit - 1).Select(value => value.Name).ToListAsync(token);
        results.AddRange(holidays.Select(name => new SearchResultItem("event", name, date.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("tr-TR")),
            "holiday-transfer", new Dictionary<string, string> { ["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, "Event")));
        return results;
    }

    private async Task<IReadOnlyList<SearchResultItem>> SearchCalendarEventsAsync(string term, CancellationToken token)
    {
        var normalized = TurkishSearchText.Normalize(term);
        var values = await db.Holidays.AsNoTracking().Where(value => value.SearchName.StartsWith(normalized))
            .OrderByDescending(value => value.Date).Take(GroupLimit)
            .Select(value => new { value.Name, value.Date }).ToListAsync(token);
        return values.Select(value => new SearchResultItem("event", value.Name,
            value.Date.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("tr-TR")), "holiday-transfer",
            new Dictionary<string, string> { ["date"] = value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }, "Event")).ToArray();
    }

    private static SearchResultItem[] SearchModules(string term, IReadOnlySet<string> permissions)
    {
        var modules = new (string Title, string Route, string Icon, string[] Permissions)[]
        {
            ("Dashboard", "dashboard", "Dashboard", ["dashboard.read"]),
            ("Günlük Takip", "daily-tracking", "Tracking", ["access.read"]),
            ("Öğrenciler", "students", "Person", ["students.read"]),
            ("Yemek Hakedişleri", "entitlements", "Meal", ["entitlements.manage", "entitlements.bulk"]),
            ("Takvim / Tatil", "holiday-transfer", "Calendar", ["calendar.manage"]),
            ("Cihazlar / Turnikeler", "devices", "Device", ["devices.read", "devices.manage"]),
            ("SMS Merkezi", "sms", "Message", ["sms.read", "sms.send", "sms.manage"]),
            ("Kasa", "cash", "Cash", ["cash.read"]),
            ("Raporlar", "reports", "Report", ["reports.read"]),
            ("Ayarlar", "settings", "Settings", ["settings.read", "settings.manage"])
        };
        return modules.Where(module => module.Permissions.Any(permissions.Contains)
                && CultureInfo.GetCultureInfo("tr-TR").CompareInfo.IndexOf(module.Title, term, CompareOptions.IgnoreCase) >= 0)
            .Take(GroupLimit)
            .Select(module => new SearchResultItem("module", module.Title, "Modülü aç", module.Route,
                new Dictionary<string, string>(), module.Icon)).ToArray();
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
    private static void Add(List<SearchResultGroup> groups, string type, string title, IReadOnlyList<SearchResultItem> items)
    {
        if (items.Count > 0) groups.Add(new(type, title, items));
    }
}

public static class TurkishDateParser
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly string[] ExactFormats = ["yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy", "dd.MM", "d.M"];

    public static bool TryParse(string input, DateOnly today, out DateOnly date)
    {
        var value = input.Trim();
        if (DateOnly.TryParseExact(value, ExactFormats[..3], CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return true;
        if (DateOnly.TryParseExact(value, ExactFormats[3..], CultureInfo.InvariantCulture, DateTimeStyles.None, out var shortDate))
        {
            date = new DateOnly(today.Year, shortDate.Month, shortDate.Day);
            return true;
        }
        if (DateOnly.TryParse(value, Turkish, DateTimeStyles.AllowWhiteSpaces, out date))
        {
            if (!ContainsYear(value)) date = new DateOnly(today.Year, date.Month, date.Day);
            return true;
        }
        date = default;
        return false;
    }

    private static bool ContainsYear(string value) => value.Any(char.IsDigit) && value.Count(char.IsDigit) >= 5;
}
