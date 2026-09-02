using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Yemekhane.Application.Students;
using Yemekhane.Application.Cards;
using Yemekhane.Infrastructure.Cards;
using Yemekhane.Application.Parents;
using Yemekhane.Infrastructure.Parents;
using Yemekhane.Application.Organization;
using Yemekhane.Infrastructure.Organization;
using Yemekhane.Application.Meals;
using Yemekhane.Infrastructure.Meals;
using Yemekhane.Application.Entitlements;
using Yemekhane.Infrastructure.Entitlements;
using Yemekhane.Application.Calendar;
using Yemekhane.Infrastructure.Calendar;
using Yemekhane.Application.Leaves;
using Yemekhane.Infrastructure.Leaves;
using Yemekhane.Application.Access;
using Yemekhane.Infrastructure.Access;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Students;
using Yemekhane.Application.Sync;
using Yemekhane.Infrastructure.Sync;
using Yemekhane.Application.Sms;
using Yemekhane.Infrastructure.Sms;
using Yemekhane.Application.Income;
using Yemekhane.Infrastructure.Income;
using Yemekhane.Application.Cash;
using Yemekhane.Infrastructure.Cash;
using Yemekhane.Application.Reports;
using Yemekhane.Infrastructure.Reports;
using Yemekhane.Application.StudentImports;
using Yemekhane.Infrastructure.StudentImports;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Devices;
using Yemekhane.Infrastructure.Audit;
using Yemekhane.Infrastructure.Devices;
using Yemekhane.Application.Dashboard;
using Yemekhane.Infrastructure.Dashboard;
using Yemekhane.Application.DailyTracking;
using Yemekhane.Infrastructure.DailyTracking;
using Yemekhane.Application.BulkOperations;
using Yemekhane.Infrastructure.BulkOperations;
using Yemekhane.Application.Settings;
using Yemekhane.Infrastructure.Settings;
using Yemekhane.Application.Search;
using Yemekhane.Infrastructure.Search;
using Yemekhane.Application.Notifications;
using Yemekhane.Infrastructure.Notifications;

namespace Yemekhane.Infrastructure;

public static class InfrastructureRegistration
{
    public static IServiceCollection AddYemekhaneInfrastructure(
        this IServiceCollection services,
        string connectionString,
        int busyTimeoutSeconds = LocalDatabaseOptions.DefaultBusyTimeoutSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(busyTimeoutSeconds);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(new AccessCacheOptions());
        services.AddSingleton<AccessPerformanceMetrics>();
        services.AddSingleton<AccessSnapshotCache>();
        services.AddSingleton<IAccessCacheInvalidationSink>(provider => provider.GetRequiredService<AccessSnapshotCache>());
        services.AddSingleton<AccessCacheInvalidationInterceptor>();

        var connectionInterceptor = new SqliteConnectionPragmaInterceptor(busyTimeoutSeconds);
        services.AddSingleton(connectionInterceptor);
        services.AddDbContext<YemekhaneDbContext>((provider, options) => options
            .UseSqlite(connectionString)
            .AddInterceptors(connectionInterceptor, provider.GetRequiredService<AccessCacheInvalidationInterceptor>()));
        services.AddSingleton<LocalDatabaseHealth>();
        services.AddScoped<LocalDatabaseInitializer>();
        services.TryAddScoped<IAuditContext, SystemAuditContext>();
        services.AddScoped<IAuditRepository, EfAuditRepository>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<IStudentRepository, EfStudentRepository>();
        services.AddScoped<ICardRepository, EfCardRepository>();
        services.AddScoped<IParentRepository, EfParentRepository>();
        services.AddScoped<IOrganizationRepository, EfOrganizationRepository>();
        services.AddScoped<IMealTypeRepository, EfMealTypeRepository>();
        services.AddScoped<IMealEntitlementRepository, EfMealEntitlementRepository>();
        services.AddScoped<IMealTransferRepository, EfMealTransferRepository>();
        services.AddScoped<IHolidayRepository, EfHolidayRepository>();
        services.AddScoped<ICalendarRepository, EfCalendarRepository>();
        services.AddScoped<ICalendarClosureProvider>(provider => provider.GetRequiredService<IHolidayRepository>());
        services.AddScoped<ILeaveRepository, EfLeaveRepository>();
        services.AddScoped<IAccessDecisionRepository, EfAccessDecisionRepository>();
        services.AddScoped<ITurnstileEventStore, EfTurnstileEventStore>();
        services.AddScoped<ISyncOperationStore, EfSyncOperationStore>();
        services.AddScoped<ISmsTemplateRepository, EfSmsTemplateRepository>();
        services.AddScoped<EfSmsLogRepository>();
        services.AddScoped<ISmsLogRepository>(provider => provider.GetRequiredService<EfSmsLogRepository>());
        services.AddScoped<IBulkSmsRepository, EfBulkSmsRepository>();
        // Otomatik SMS kurallari: gelir/kart servisleri ISmsAutomationTrigger'i istege bagli alir.
        services.AddScoped<ISmsAutomationStore, EfSmsAutomationStore>();
        services.AddScoped<ISmsAutomationRepository, EfSmsAutomationRepository>();
        services.AddScoped<SmsAutomationService>();
        services.AddScoped<ISmsAutomationTrigger>(provider => provider.GetRequiredService<SmsAutomationService>());
        services.AddScoped<IIncomeRepository, EfIncomeRepository>();
        services.AddScoped<ICashRepository, EfCashRepository>();
        services.AddScoped<IReportRepository, EfReportRepository>();
        services.AddScoped<IDashboardRepository, EfDashboardRepository>();
        services.AddScoped<IDailyTrackingRepository, EfDailyTrackingRepository>();
        services.AddScoped<IBulkOperationRepository, EfBulkOperationRepository>();
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<IGlobalSearchRepository, EfGlobalSearchRepository>();
        services.AddScoped<INotificationRepository, EfNotificationRepository>();
        services.AddSingleton<ISecretProtector>(_ => OperatingSystem.IsWindows()
            ? new WindowsDpapiSecretProtector()
            : throw new PlatformNotSupportedException("Ayar gizlileri Windows DPAPI gerektirir."));
        services.AddSingleton<StudentImportPreviewStore>();
        services.AddScoped<IStudentImportService, StudentImportService>();
        services.AddScoped<IDeviceCardSyncService, DeviceCardSyncService>();
        // Fotograflar veritabaninin YANINDAKI photos/ klasorune yazilir: yedek/geri yukleme
        // ve veri klasoru gocu ayni koku paylasir. Program.cs'e dokunmadan baglanti dizgisinden turetilir.
        services.AddSingleton<IStudentPhotoStore>(new FileStudentPhotoStore(FileStudentPhotoStore.ResolveRoot(connectionString)));
        services.AddScoped<StudentPhotoService>();
        services.AddScoped<IDeviceCardListQuery, EfDeviceCardListQuery>();
        return services;
    }

    public static IServiceCollection AddYemekhaneBackup(
        this IServiceCollection services,
        string connectionString,
        BackupOptions options,
        IReadOnlyDictionary<string, string?>? systemSettings = null)
    {
        services.AddSingleton(options);
        services.AddSingleton(new BackupService(connectionString, options, systemSettings));
        services.AddHostedService<BackupBackgroundWorker>();
        return services;
    }
}
