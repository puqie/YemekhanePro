using Microsoft.AspNetCore.Authorization;

namespace Yemekhane.Api.Authorization;

public static class Permissions
{
    public const string StudentsRead = "students.read";
    public const string StudentsWrite = "students.write";
    public const string StudentsDeactivate = "students.deactivate";
    // Reserved for field-level masking when sensitive student projections are introduced.
    public const string StudentsSensitiveRead = "students.sensitive.read";
    public const string CardsManage = "cards.manage";
    public const string EntitlementsManage = "entitlements.manage";
    public const string EntitlementsBulk = "entitlements.bulk";
    public const string CalendarManage = "calendar.manage";
    public const string DevicesManage = "devices.manage";
    public const string DevicesRead = "devices.read";
    public const string ReportsRead = "reports.read";
    public const string ReportsExport = "reports.export";
    public const string CashRead = "cash.read";
    public const string CashWrite = "cash.write";
    public const string CashManage = "cash.manage";
    public const string SmsRead = "sms.read";
    public const string SmsSend = "sms.send";
    public const string SmsManage = "sms.manage";
    public const string SettingsManage = "settings.manage";
    public const string SettingsRead = "settings.read";
    public const string UsersManage = "users.manage";
    public const string BackupsManage = "backups.manage";
    public const string AuditRead = "audit.read";
    public const string DashboardRead = "dashboard.read";
    public const string AccessRead = "access.read";
    public const string NotificationsRead = "notifications.read";

    public const string ClaimType = "permission";
    public const string PolicyPrefix = "Permission:";

    public static readonly IReadOnlyList<string> All =
    [
        StudentsRead, StudentsWrite, StudentsDeactivate, StudentsSensitiveRead, CardsManage, EntitlementsManage, EntitlementsBulk,
        CalendarManage, DevicesRead, DevicesManage, ReportsRead, ReportsExport, CashRead, CashWrite, CashManage, SmsRead, SmsSend,
        SmsManage, SettingsRead, SettingsManage, UsersManage, BackupsManage, AuditRead, DashboardRead, AccessRead, NotificationsRead
    ];

    public static string Policy(string permission) => PolicyPrefix + permission;
}

public sealed class PermissionAuthorizeAttribute(string permission) : AuthorizeAttribute(Permissions.Policy(permission));
