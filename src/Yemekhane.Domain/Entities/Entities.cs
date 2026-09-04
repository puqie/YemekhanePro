namespace Yemekhane.Domain.Entities;

public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class Student : Entity
{
    public required string StudentNo { get; set; }
    public string? NationalId { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    /// <summary>Türkçe normalleştirilmiş "ad soyad"; arama bu sütun üzerinden yapılır.</summary>
    public string SearchName { get; set; } = string.Empty;
    public DateOnly? BirthDate { get; set; }
    public Guid? ClassId { get; set; }
    public Guid? SectionId { get; set; }
    public Guid? DepartmentId { get; set; }
    public Guid? JobId { get; set; }
    public string? FingerprintId { get; set; }
    public string? Pid { get; set; }
    public string? Address { get; set; }
    public string? PhotoPath { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDeleted { get; set; }
    public DateOnly RegisteredOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);
}

public sealed class StudentCard : Entity
{
    public Guid StudentId { get; set; }
    public required string CardNumber { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public string? ReplacementReason { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Parent : Entity
{
    public Guid StudentId { get; set; }
    public required string Name { get; set; }
    public required string NormalizedPhone { get; set; }
    public string? Relationship { get; set; }
    public bool IsPrimary { get; set; } = true;
    public bool IsActive { get; set; } = true;
}

public sealed class SchoolClass : Entity
{
    public required string Name { get; set; }
    /// <summary>Türkçe normalleştirilmiş ad; arama bu sütun üzerinden yapılır.</summary>
    public string SearchName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
public sealed class Section : Entity { public required string Name { get; set; } }
public sealed class Department : Entity { public required string Name { get; set; } }
public sealed class Job : Entity { public required string Name { get; set; } }

public sealed class StudentGroup : Entity
{
    public required string Name { get; set; }
    /// <summary>Türkçe normalleştirilmiş ad; arama bu sütun üzerinden yapılır.</summary>
    public string SearchName { get; set; } = string.Empty;
    public required string GroupType { get; set; }
    public string? CriteriaJson { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class StudentGroupMember
{
    public Guid GroupId { get; set; }
    public Guid StudentId { get; set; }
}

public sealed class MealType : Entity
{
    public required string Name { get; set; }
    public TimeOnly? StartsAt { get; set; }
    public TimeOnly? EndsAt { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class MealEntitlement : Entity
{
    public Guid StudentId { get; set; }
    public Guid MealTypeId { get; set; }
    public DateOnly EntitlementDate { get; set; }
    public int Quantity { get; set; } = 1;
    public int ConsumedQuantity { get; set; }
    public required string Status { get; set; }
    public string? Source { get; set; }
    public long Version { get; set; }
}

public sealed class MealUsage : Entity
{
    public Guid EntitlementId { get; set; }
    public Guid StudentId { get; set; }
    public Guid MealTypeId { get; set; }
    public Guid AccessLogId { get; set; }
    public DateTimeOffset UsedAt { get; set; }
}

public sealed class AccessLog : Entity
{
    public DateTimeOffset Timestamp { get; set; }
    public Guid? CardId { get; set; }
    public Guid? StudentId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid? MealTypeId { get; set; }
    public required string CardNumber { get; set; }
    public required string Decision { get; set; }
    public required string Reason { get; set; }
    public required string Direction { get; set; }
    public required string ReaderSource { get; set; }
    public Guid? OperatorId { get; set; }
    public Guid OperationId { get; set; }
}

/// <summary>
/// Bir kartin tek bir cihazdaki senkronizasyon durumu. Kart basina tek satir yerine
/// kart-cihaz cifti basina satir tutulur: cok turnikeli kurulumda bir cihazda eksik kalan
/// kart ancak boyle gorulebilir.
/// </summary>
public sealed class DeviceCardState : Entity
{
    public Guid DeviceId { get; set; }
    public Guid CardId { get; set; }
    public Guid StudentId { get; set; }
    public required string CardNumber { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}

public sealed class Device : Entity
{
    public required string Name { get; set; }
    public required string DeviceType { get; set; }
    public required string ConnectionType { get; set; }
    public string? ComPort { get; set; }
    public int? BaudRate { get; set; }
    public string? IpAddress { get; set; }
    public int? IpPort { get; set; }
    public bool IsActive { get; set; } = true;
    public bool AutoConnect { get; set; }
    public bool HasTurnstile { get; set; }

    /// <summary>
    /// Turnike role darbe suresi (ms). Uretici dokumaninda belgelenmedigi icin kurulumda
    /// sahada dogrulanir; bu yuzden sabit degil, cihaz basina yapilandirilabilir.
    /// Null ise OzakTurnstileProfile varsayilani kullanilir.
    /// </summary>
    public int? TurnstileRelayPulseMs { get; set; }

    /// <summary>
    /// Turnike her iki yonde de surulebiliyor mu. Sahadaki mekanik yonlendirme tek yone
    /// kilitlenmis olabilir; cift yon varsayilmaz, kurulumda bildirilir.
    /// </summary>
    public bool TurnstileBidirectional { get; set; }

    public string? Location { get; set; }
    public required string Direction { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public DateTimeOffset? LastStatusAt { get; set; }
    public string? Model { get; set; }
    public string? SerialNumber { get; set; }
    public string? Firmware { get; set; }
    public required string ConnectionStatus { get; set; }
}

public sealed class DeviceEvent : Entity
{
    public Guid DeviceId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string EventType { get; set; }
    public required string Severity { get; set; }
    public required string Message { get; set; }
    public string? PayloadJson { get; set; }
}

public sealed class TurnstileEvent : Entity
{
    public Guid DeviceId { get; set; }
    public Guid? AccessLogId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Command { get; set; }
    public required string Result { get; set; }
    public string? Error { get; set; }
}

public sealed class Holiday : Entity
{
    public DateOnly Date { get; set; }
    public required string Name { get; set; }
    /// <summary>Türkçe normalleştirilmiş ad; arama bu sütun üzerinden yapılır.</summary>
    public string SearchName { get; set; } = string.Empty;
    public required string HolidayType { get; set; }
    public string? Description { get; set; }
    public required string TransferBehavior { get; set; }
}

public sealed class HolidayScope : Entity
{
    public Guid HolidayId { get; set; }
    public required string ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
}

public sealed class ScheduleOverride : Entity
{
    public DateOnly Date { get; set; }
    public required string ExceptionType { get; set; }
    public required string ScopeType { get; set; }
    public Guid? ScopeId { get; set; }
    public Guid? MealTypeId { get; set; }
    public required string EntitlementBehavior { get; set; }
    public DateOnly? TargetDate { get; set; }
    public string? Description { get; set; }
    public Guid CreatedBy { get; set; }
}

public sealed class MealTransfer : Entity
{
    public Guid StudentId { get; set; }
    public Guid MealTypeId { get; set; }
    public Guid SourceEntitlementId { get; set; }
    public DateOnly OriginalDate { get; set; }
    public DateOnly TargetDate { get; set; }
    public int Quantity { get; set; }
    public required string Reason { get; set; }
    public Guid CreatedBy { get; set; }
    public Guid? BulkOperationId { get; set; }
}

public sealed class StudentLeave : Entity
{
    public Guid StudentId { get; set; }
    public DateOnly StartsOn { get; set; }
    public DateOnly EndsOn { get; set; }
    public required string LeaveType { get; set; }
    public string? Description { get; set; }
    public required string EntitlementBehavior { get; set; }
}

public sealed class IncomeType : Entity { public required string Name { get; set; } public bool IsActive { get; set; } = true; }

public sealed class IncomeTransaction : Entity
{
    public Guid OperationId { get; set; }
    public Guid? StudentId { get; set; }
    public Guid IncomeTypeId { get; set; }
    public string? CardNumber { get; set; }
    public DateTimeOffset TransactionAt { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }
    public Guid CreatedBy { get; set; }
    public bool IsVoided { get; set; }
    public DateTimeOffset? VoidedAt { get; set; }
    public Guid? VoidedBy { get; set; }
    public string? VoidReason { get; set; }
}

public sealed class SmsTemplate : Entity { public required string Name { get; set; } public required string Body { get; set; } public bool IsActive { get; set; } = true; }

public sealed class SmsLog : Entity
{
    public Guid? StudentId { get; set; }
    public Guid? TemplateId { get; set; }
    public required string Phone { get; set; }
    public required string Message { get; set; }
    public string? Provider { get; set; }
    public required string Status { get; set; }
    public required string IdempotencyKey { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? SendingStartedAt { get; set; }
    public string? ClaimToken { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? Error { get; set; }
}

public sealed class User : Entity
{
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsActive { get; set; } = true;
    public int FailedLoginAttempts { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
}
public sealed class Role : Entity
{
    public required string Name { get; set; }
    public required string NormalizedName { get; set; }
    public bool IsBuiltIn { get; set; }
}
public sealed class PermissionDefinition : Entity { public required string Code { get; set; } public required string Name { get; set; } }
public sealed class UserRole { public Guid UserId { get; set; } public Guid RoleId { get; set; } }
public sealed class RolePermissionAssignment { public Guid RoleId { get; set; } public Guid PermissionId { get; set; } }

public sealed class AuditLog : Entity
{
    public Guid? UserId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Action { get; set; }
    public required string EntityName { get; set; }
    public string? EntityId { get; set; }
    public required string Description { get; set; }
    public int AffectedRecords { get; set; }
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public Guid? BulkOperationId { get; set; }
    public string? CorrelationId { get; set; }
}

public sealed class BulkOperation : Entity
{
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public required string OperationType { get; set; }
    public required string Status { get; set; }
    public required string RequestJson { get; set; }
    public required string ResultJson { get; set; }
    public string? UndoJson { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }
}

public sealed class SyncOperation : Entity
{
    public Guid OperationId { get; set; }
    public required string EntityName { get; set; }
    public required string EntityId { get; set; }
    public required string OperationType { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string DeviceId { get; set; }
    public required string Payload { get; set; }
    public required string SyncStatus { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}

public sealed class SystemSetting : Entity
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public bool IsSecret { get; set; }
}

public sealed class Notification : Entity
{
    public required string Severity { get; set; }
    public required string Type { get; set; }
    public required string Title { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? RelatedEntityType { get; set; }
    public string? RelatedEntityId { get; set; }
    public string? RelatedRoute { get; set; }
    public string? RouteParametersJson { get; set; }
    public string? AudiencePermission { get; set; }
    public Guid? AudienceUserId { get; set; }
    public string? DeduplicationKey { get; set; }
    public string? DeduplicationSlot { get; set; }
    public int Count { get; set; } = 1;
    public DateTimeOffset LatestAt { get; set; }
    public DateTimeOffset RetainUntil { get; set; }
}

public sealed class NotificationReceipt
{
    public Guid NotificationId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
}
