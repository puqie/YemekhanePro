using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Devices;
using Yemekhane.Domain.Entities;

namespace Yemekhane.Infrastructure.Persistence;

public sealed class YemekhaneDbContext(DbContextOptions<YemekhaneDbContext> options) : DbContext(options)
{
    [DbFunction("julianday", IsBuiltIn = true)]
    public static double JulianDay(DateTimeOffset value) =>
        throw new InvalidOperationException("Bu metot yalnızca SQL sorgularında kullanılabilir.");

    [DbFunction("round", IsBuiltIn = true)]
    public static double Round(double value) =>
        throw new InvalidOperationException("Bu metot yalnızca SQL sorgularında kullanılabilir.");

    public DbSet<Student> Students => Set<Student>();
    public DbSet<StudentCard> StudentCards => Set<StudentCard>();
    public DbSet<Parent> Parents => Set<Parent>();
    public DbSet<MealEntitlement> MealEntitlements => Set<MealEntitlement>();
    public DbSet<MealUsage> MealUsages => Set<MealUsage>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceCardState> DeviceCardStates => Set<DeviceCardState>();
    public DbSet<DeviceEvent> DeviceEvents => Set<DeviceEvent>();
    public DbSet<TurnstileEvent> TurnstileEvents => Set<TurnstileEvent>();
    public DbSet<Holiday> Holidays => Set<Holiday>();
    public DbSet<MealTransfer> MealTransfers => Set<MealTransfer>();
    public DbSet<SyncOperation> SyncOperations => Set<SyncOperation>();
    public DbSet<SmsTemplate> SmsTemplates => Set<SmsTemplate>();
    public DbSet<SmsLog> SmsLogs => Set<SmsLog>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<PermissionDefinition> Permissions => Set<PermissionDefinition>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermissionAssignment> RolePermissions => Set<RolePermissionAssignment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<BulkOperation> BulkOperations => Set<BulkOperation>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationReceipt> NotificationReceipts => Set<NotificationReceipt>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureAuditLogsAreImmutable();
        RefreshSearchNames();
        SyncDeviceCardQueue();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureAuditLogsAreImmutable();
        RefreshSearchNames();
        SyncDeviceCardQueue();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureTables(modelBuilder);
        ConfigureIdentityAndOrganization(modelBuilder);
        ConfigureMealsAndCalendar(modelBuilder);
        ConfigureOperations(modelBuilder);
        ConfigureSecurityAndSystem(modelBuilder);
    }

    private static void ConfigureTables(ModelBuilder b)
    {
        b.Entity<Student>().ToTable("students"); b.Entity<StudentCard>().ToTable("student_cards"); b.Entity<Parent>().ToTable("parents");
        b.Entity<SchoolClass>().ToTable("classes"); b.Entity<Section>().ToTable("sections"); b.Entity<Department>().ToTable("departments"); b.Entity<Job>().ToTable("jobs");
        b.Entity<StudentGroup>().ToTable("student_groups"); b.Entity<StudentGroupMember>().ToTable("student_group_members"); b.Entity<MealType>().ToTable("meal_types");
        b.Entity<MealEntitlement>().ToTable("meal_entitlements"); b.Entity<MealUsage>().ToTable("meal_usage"); b.Entity<AccessLog>().ToTable("access_logs");
        b.Entity<Device>().ToTable("devices"); b.Entity<DeviceCardState>().ToTable("device_card_states"); b.Entity<DeviceEvent>().ToTable("device_events"); b.Entity<TurnstileEvent>().ToTable("turnstile_events");
        // Turkce normallestirilmis arama sutunlari ve indeksleri.
        foreach (var (entity, index) in new (Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder, string)[]
                 {
                     (b.Entity<SchoolClass>(), "ix_classes_search_name"),
                     (b.Entity<StudentGroup>(), "ix_student_groups_search_name"),
                     (b.Entity<Holiday>(), "ix_holidays_search_name")
                 })
        {
            entity.Property("SearchName").HasMaxLength(TurkishSearchText.MaxLength).HasDefaultValue(string.Empty);
            entity.HasIndex("SearchName").HasDatabaseName(index);
        }

        b.Entity<Holiday>().ToTable("holidays"); b.Entity<HolidayScope>().ToTable("holiday_scopes"); b.Entity<ScheduleOverride>().ToTable("schedule_exceptions");
        b.Entity<MealTransfer>().ToTable("meal_transfers"); b.Entity<StudentLeave>().ToTable("student_leaves"); b.Entity<IncomeType>().ToTable("income_types");
        b.Entity<IncomeTransaction>().ToTable("income_transactions"); b.Entity<SmsTemplate>().ToTable("sms_templates"); b.Entity<SmsLog>().ToTable("sms_logs");
        b.Entity<User>().ToTable("users"); b.Entity<Role>().ToTable("roles"); b.Entity<PermissionDefinition>().ToTable("permissions"); b.Entity<UserRole>().ToTable("user_roles");
        b.Entity<RolePermissionAssignment>().ToTable("role_permissions"); b.Entity<AuditLog>().ToTable("audit_logs"); b.Entity<BulkOperation>().ToTable("bulk_operations");
        b.Entity<SyncOperation>().ToTable("sync_operations"); b.Entity<SystemSetting>().ToTable("system_settings");
        b.Entity<Notification>().ToTable("notifications"); b.Entity<NotificationReceipt>().ToTable("notification_receipts");
    }

    private static void ConfigureIdentityAndOrganization(ModelBuilder b)
    {
        b.Entity<Student>(e => { e.HasIndex(x => x.StudentNo).IsUnique().HasDatabaseName("ix_students_student_no"); e.HasIndex(x => x.ClassId).HasDatabaseName("ix_students_class_id"); e.HasIndex(x => new { x.LastName, x.FirstName }); e.HasIndex(x => x.FirstName).HasDatabaseName("ix_students_first_name"); e.Property(x => x.StudentNo).HasMaxLength(32).HasColumnName("student_no"); e.Property(x => x.NationalId).HasMaxLength(11).HasColumnName("national_id"); e.HasQueryFilter(x => !x.IsDeleted);
            // Turkce normallestirilmis arama sutunu: LIKE ASCII disinda buyuk/kucuk harf duyarsiz degil.
            e.Property(x => x.SearchName).HasMaxLength(TurkishSearchText.MaxLength).HasDefaultValue(string.Empty);
            e.HasIndex(x => x.SearchName).HasDatabaseName("ix_students_search_name"); });
        b.Entity<StudentCard>(e => { e.HasIndex(x => x.CardNumber).IsUnique().HasDatabaseName("ix_student_cards_card_number"); e.HasIndex(x => new { x.StudentId, x.IsActive }); e.HasIndex(x => x.StudentId).IsUnique().HasFilter("IsActive = 1").HasDatabaseName("ux_student_cards_one_active"); e.Property(x => x.CardNumber).HasMaxLength(128).HasColumnName("card_number"); e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Parent>(e => { e.HasIndex(x => x.NormalizedPhone); e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<Student>(e => { e.HasOne<SchoolClass>().WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.SetNull); e.HasOne<Section>().WithMany().HasForeignKey(x => x.SectionId).OnDelete(DeleteBehavior.SetNull); e.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.SetNull); e.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.SetNull); });
        b.Entity<StudentGroupMember>(e => { e.HasKey(x => new { x.GroupId, x.StudentId }); e.HasOne<StudentGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.Cascade); e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<SchoolClass>().HasIndex(x => x.Name).IsUnique(); b.Entity<Section>().HasIndex(x => x.Name).IsUnique(); b.Entity<Department>().HasIndex(x => x.Name).IsUnique(); b.Entity<Job>().HasIndex(x => x.Name).IsUnique(); b.Entity<StudentGroup>().HasIndex(x => x.Name).IsUnique();
    }

    private static void ConfigureMealsAndCalendar(ModelBuilder b)
    {
        b.Entity<MealType>().HasIndex(x => x.Name).IsUnique();
        b.Entity<MealEntitlement>(e => { e.HasIndex(x => new { x.StudentId, x.EntitlementDate, x.MealTypeId }).IsUnique().HasDatabaseName("ux_meal_entitlements_student_date_meal"); e.HasIndex(x => x.StudentId).HasDatabaseName("ix_meal_entitlements_student_id"); e.HasIndex(x => x.EntitlementDate).HasDatabaseName("ix_meal_entitlements_date"); e.Property(x => x.Version).IsConcurrencyToken(); e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict); e.HasOne<MealType>().WithMany().HasForeignKey(x => x.MealTypeId).OnDelete(DeleteBehavior.Restrict); e.ToTable(t => { t.HasCheckConstraint("ck_meal_entitlement_quantity", "Quantity > 0"); t.HasCheckConstraint("ck_meal_entitlement_consumed", "ConsumedQuantity >= 0 AND ConsumedQuantity <= Quantity"); }); });
        b.Entity<MealUsage>(e => { e.HasIndex(x => x.AccessLogId).IsUnique(); e.HasIndex(x => new { x.StudentId, x.UsedAt }); e.HasOne<MealEntitlement>().WithMany().HasForeignKey(x => x.EntitlementId).OnDelete(DeleteBehavior.Restrict); e.HasOne<AccessLog>().WithOne().HasForeignKey<MealUsage>(x => x.AccessLogId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<Holiday>(e => { e.HasIndex(x => x.Date); e.HasIndex(x => x.Name).HasDatabaseName("ix_holidays_name"); }); b.Entity<HolidayScope>(e => { e.HasIndex(x => new { x.HolidayId, x.ScopeType, x.ScopeId }).IsUnique(); e.HasOne<Holiday>().WithMany().HasForeignKey(x => x.HolidayId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ScheduleOverride>().HasIndex(x => new { x.Date, x.ScopeType, x.ScopeId });
        b.Entity<MealTransfer>(e => { e.HasIndex(x => new { x.StudentId, x.OriginalDate }); e.HasIndex(x => x.TargetDate); e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict); e.HasOne<MealEntitlement>().WithMany().HasForeignKey(x => x.SourceEntitlementId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<StudentLeave>(e => { e.HasIndex(x => new { x.StudentId, x.StartsOn, x.EndsOn }); e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict); });
    }

    private static void ConfigureOperations(ModelBuilder b)
    {
        b.Entity<AccessLog>(e => { e.HasIndex(x => x.CardNumber).HasDatabaseName("ix_access_logs_card_number"); e.HasIndex(x => new { x.StudentId, x.Timestamp }); e.HasIndex(x => x.OperationId).IsUnique(); e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<DeviceCardState>(e =>
        {
            // Kart-cihaz cifti benzersizdir: ayni kart icin ayni cihazda iki satir olusursa
            // biri "yuklendi" digeri "bekliyor" gorunur ve gercek durum belirsizlesir.
            e.HasIndex(x => new { x.DeviceId, x.CardId }).IsUnique();
            e.HasIndex(x => new { x.DeviceId, x.Status });
            e.HasIndex(x => x.CardId);
            e.Property(x => x.CardNumber).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.LastError).HasMaxLength(512);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<StudentCard>().WithMany().HasForeignKey(x => x.CardId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Device>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => new { x.IpAddress, x.IpPort }).IsUnique()
                .HasFilter("IpAddress IS NOT NULL AND IpPort IS NOT NULL");
            e.HasIndex(x => new { x.ComPort, x.BaudRate }).IsUnique()
                .HasFilter("ComPort IS NOT NULL AND BaudRate IS NOT NULL");
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.DeviceType).HasMaxLength(32);
            e.Property(x => x.ConnectionType).HasMaxLength(16);
            e.Property(x => x.ComPort).HasMaxLength(32);
            e.Property(x => x.IpAddress).HasMaxLength(255);
            e.Property(x => x.Location).HasMaxLength(150);
            e.Property(x => x.Direction).HasMaxLength(16);
            e.Property(x => x.ConnectionStatus).HasMaxLength(24);
            e.Property(x => x.Model).HasMaxLength(100);
            e.Property(x => x.SerialNumber).HasMaxLength(100);
            e.Property(x => x.Firmware).HasMaxLength(100);
        });
        b.Entity<DeviceEvent>(e => { e.HasIndex(x => new { x.DeviceId, x.Timestamp }); e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<TurnstileEvent>(e => { e.HasIndex(x => new { x.DeviceId, x.Timestamp }); e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<IncomeType>(e => { e.HasIndex(x => x.Name).IsUnique(); e.Property(x => x.Name).HasMaxLength(100).UseCollation("NOCASE"); });
        b.Entity<IncomeTransaction>(e =>
        {
            e.HasIndex(x => x.OperationId).IsUnique();
            e.HasIndex(x => x.TransactionAt);
            e.HasIndex(x => new { x.IncomeTypeId, x.TransactionAt });
            e.HasIndex(x => new { x.StudentId, x.TransactionAt });
            e.Property(x => x.Amount).HasPrecision(18, 2);
            e.Property(x => x.CardNumber).HasMaxLength(128);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.VoidReason).HasMaxLength(500);
            e.HasOne<Student>().WithMany().HasForeignKey(x => x.StudentId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<IncomeType>().WithMany().HasForeignKey(x => x.IncomeTypeId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_income_transactions_amount", "Amount > 0"));
        });
        b.Entity<SmsTemplate>(e => { e.HasIndex(x => x.Name).IsUnique(); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Body).HasMaxLength(1600); });
        b.Entity<SmsLog>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => new { x.Status, x.NextAttemptAt, x.CreatedAt });
            e.Property(x => x.Phone).HasMaxLength(13);
            e.Property(x => x.Message).HasMaxLength(1600);
            e.Property(x => x.Provider).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.Property(x => x.ClaimToken).HasMaxLength(32);
            e.Property(x => x.ProviderMessageId).HasMaxLength(256);
            e.Property(x => x.Error).HasMaxLength(500);
        });
    }

    private static void ConfigureSecurityAndSystem(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(x => x.NormalizedUsername).IsUnique().HasDatabaseName("ux_users_normalized_username");
            e.Property(x => x.Username).HasMaxLength(128);
            e.Property(x => x.NormalizedUsername).HasMaxLength(128);
            e.Property(x => x.PasswordHash).HasMaxLength(512);
            e.Property(x => x.SecurityStamp).HasMaxLength(32);
        });
        b.Entity<Role>(e => { e.HasIndex(x => x.NormalizedName).IsUnique().HasDatabaseName("ux_roles_normalized_name"); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.NormalizedName).HasMaxLength(100); });
        b.Entity<PermissionDefinition>(e => { e.HasIndex(x => x.Code).IsUnique(); e.Property(x => x.Code).HasMaxLength(100); e.Property(x => x.Name).HasMaxLength(150); });
        b.Entity<UserRole>(e => { e.HasKey(x => new { x.UserId, x.RoleId }); e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade); e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<RolePermissionAssignment>(e => { e.HasKey(x => new { x.RoleId, x.PermissionId }); e.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade); e.HasOne<PermissionDefinition>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.Action);
            e.HasIndex(x => x.BulkOperationId);
            e.HasIndex(x => x.CorrelationId);
            e.HasIndex(x => new { x.EntityName, x.EntityId });
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.EntityName).HasMaxLength(100);
            e.Property(x => x.EntityId).HasMaxLength(128);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.CorrelationId).HasMaxLength(128);
        });
        b.Entity<BulkOperation>(e =>
        {
            e.HasIndex(x => x.IdempotencyKey).IsUnique();
            e.HasIndex(x => x.CreatedAt);
            e.Property(x => x.IdempotencyKey).HasMaxLength(128);
            e.Property(x => x.RequestHash).HasMaxLength(64);
            e.Property(x => x.OperationType).HasMaxLength(40);
            e.Property(x => x.Status).HasMaxLength(24);
        });
        b.Entity<SyncOperation>(e => { e.HasIndex(x => x.OperationId).IsUnique(); e.HasIndex(x => new { x.SyncStatus, x.Timestamp }); });
        b.Entity<SystemSetting>().HasIndex(x => x.Key).IsUnique();
        b.Entity<Notification>(e =>
        {
            e.HasIndex(x => new { x.LatestAt, x.Id });
            e.HasIndex(x => x.RetainUntil);
            e.HasIndex(x => x.DeduplicationKey);
            e.HasIndex(x => x.DeduplicationSlot).IsUnique().HasFilter("DeduplicationSlot IS NOT NULL")
                .HasDatabaseName("ux_notifications_deduplication_slot");
            e.HasIndex(x => x.AudienceUserId);
            e.Property(x => x.Severity).HasMaxLength(16);
            e.Property(x => x.Type).HasMaxLength(80);
            e.Property(x => x.Title).HasMaxLength(200);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.Property(x => x.RelatedEntityType).HasMaxLength(100);
            e.Property(x => x.RelatedEntityId).HasMaxLength(128);
            e.Property(x => x.RelatedRoute).HasMaxLength(250);
            e.Property(x => x.RouteParametersJson).HasMaxLength(2000);
            e.Property(x => x.AudiencePermission).HasMaxLength(100);
            e.Property(x => x.DeduplicationKey).HasMaxLength(200);
            e.Property(x => x.DeduplicationSlot).HasMaxLength(300);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.AudienceUserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<NotificationReceipt>(e =>
        {
            e.HasKey(x => new { x.NotificationId, x.UserId });
            e.HasIndex(x => new { x.UserId, x.ReadAt });
            e.HasOne<Notification>().WithMany().HasForeignKey(x => x.NotificationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    /// <summary>
    /// Aranabilir sütunları kaydetmeden önce tazeler. Tek noktada yapılır; içe aktarma, toplu işlem,
    /// seed ve API dahil bütün yazma yolları buradan geçtiği için senkronizasyon unutulamaz.
    /// </summary>
    private void RefreshSearchNames()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            switch (entry.Entity)
            {
                case Student student:
                    student.SearchName = TurkishSearchText.NormalizeFullName(student.FirstName, student.LastName);
                    break;
                case SchoolClass schoolClass:
                    schoolClass.SearchName = TurkishSearchText.Normalize(schoolClass.Name);
                    break;
                case StudentGroup group:
                    group.SearchName = TurkishSearchText.Normalize(group.Name);
                    break;
                case Holiday holiday:
                    holiday.SearchName = TurkishSearchText.Normalize(holiday.Name);
                    break;
            }
        }
    }

    /// <summary>
    /// Kart ve cihaz degisikliklerini kart-cihaz kuyruguna yansitir. SaveChanges sinirinda yapilir:
    /// tek tek cagiranlara birakilsaydi ileride eklenen bir yazma yolu bunu unutur ve kart cihaza
    /// hic gitmezdi -- ogrenci turnikeden gecemezken sistemde hicbir sorun gorunmezdi.
    /// </summary>
    private void SyncDeviceCardQueue()
    {
        var cardEntries = ChangeTracker.Entries<StudentCard>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToList();
        var newDevices = ChangeTracker.Entries<Device>()
            .Where(entry => entry.State is EntityState.Added && entry.Entity.DeviceType == "SF300" && entry.Entity.IsActive)
            .Select(entry => entry.Entity)
            .ToList();
        if (cardEntries.Count == 0 && newDevices.Count == 0) return;

        var deviceIds = Devices.Local.Where(device => device.IsActive && device.DeviceType == "SF300")
            .Select(device => device.Id)
            .Concat(Set<Device>().AsNoTracking().Where(device => device.IsActive && device.DeviceType == "SF300")
                .Select(device => device.Id))
            .Distinct()
            .ToList();

        foreach (var entry in cardEntries)
            QueueCard(entry.Entity, deviceIds);

        // Yeni eklenen cihaz mevcut aktif kartlari devralir; aksi halde cihaz bos kalir.
        foreach (var device in newDevices)
        {
            var existingCards = Set<StudentCard>().AsNoTracking().Where(card => card.IsActive).ToList();
            foreach (var card in existingCards) QueueCard(card, [device.Id]);
        }
    }

    private void QueueCard(StudentCard card, List<Guid> deviceIds)
    {
        if (deviceIds.Count == 0) return;
        var states = DeviceCardStates.Local.Where(state => state.CardId == card.Id)
            .Concat(Set<DeviceCardState>().Where(state => state.CardId == card.Id))
            .DistinctBy(state => state.DeviceId)
            .ToList();

        foreach (var deviceId in deviceIds)
        {
            var state = states.SingleOrDefault(value => value.DeviceId == deviceId);
            if (state is null)
            {
                // Pasif bir kart hic yuklenmemisse cihaza gondermeye gerek yoktur.
                if (!card.IsActive) continue;
                Add(new DeviceCardState
                {
                    DeviceId = deviceId, CardId = card.Id, StudentId = card.StudentId,
                    CardNumber = card.CardNumber, Status = DeviceCardSyncStatus.Pending
                });
                continue;
            }

            state.CardNumber = card.CardNumber;
            if (!card.IsActive)
            {
                // Cihaza hic ulasmamis kart icin silme gondermeye gerek yoktur.
                state.Status = state.Status == DeviceCardSyncStatus.Pending
                    ? DeviceCardSyncStatus.Removed
                    : DeviceCardSyncStatus.PendingRemoval;
            }
            else if (state.Status is DeviceCardSyncStatus.Removed or DeviceCardSyncStatus.PendingRemoval)
            {
                state.Status = DeviceCardSyncStatus.Pending;
            }
        }
    }

    private void EnsureAuditLogsAreImmutable()
    {
        if (ChangeTracker.Entries<AuditLog>().Any(x => x.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Audit kayıtları değiştirilemez veya silinemez.");
        if (ChangeTracker.Entries<Notification>().Any(x => x.State == EntityState.Deleted && x.Entity.RetainUntil > DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Bildirimler saklama süresi dolmadan silinemez.");
    }
}
