using Yemekhane.Application.Common;

namespace Yemekhane.Application.Sms;

public sealed record SmsSendRequest(string Phone, string Message);

public enum SmsSendOutcome
{
    Success,
    TransientFailure,
    PermanentFailure
}

public enum SmsErrorCategory
{
    None,
    Configuration,
    Authentication,
    RateLimited,
    Timeout,
    Transport,
    ProviderRejected,
    ProviderUnavailable,
    InvalidResponse
}

public sealed record SmsSendResult(
    SmsSendOutcome Outcome,
    string? ProviderMessageId = null,
    SmsErrorCategory ErrorCategory = SmsErrorCategory.None,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    int? HttpStatusCode = null)
{
    public bool IsSuccess => Outcome == SmsSendOutcome.Success;
}

public interface ISmsProvider
{
    Task<SmsSendResult> SendAsync(SmsSendRequest request, CancellationToken cancellationToken = default);
}

public static class SmsLogStatuses
{
    public const string Pending = "Pending";
    public const string Sending = "Sending";
    public const string Sent = "Sent";
    public const string Failed = "Failed";
    public const string RetryScheduled = "RetryScheduled";
}

public sealed record EnqueueSmsRequest(
    string Phone,
    string IdempotencyKey,
    string? Message = null,
    Guid? TemplateId = null,
    IReadOnlyDictionary<string, object?>? Variables = null,
    Guid? StudentId = null);

public sealed record SmsLogDetails(
    Guid Id, Guid? StudentId, Guid? TemplateId, string Phone, string Message,
    string? Provider, string Status, string IdempotencyKey, int AttemptCount,
    DateTimeOffset? NextAttemptAt, DateTimeOffset? SendingStartedAt, DateTimeOffset? SentAt,
    string? ProviderMessageId, string? Error, DateTimeOffset CreatedAt);

public sealed record SmsHistoryFilter(
    string? Status = null, string? Phone = null, DateTimeOffset? From = null,
    DateTimeOffset? To = null, int Page = 1, int PageSize = 50, Guid? StudentId = null,
    string? Provider = null, string? Student = null);

public sealed record BulkSmsScope(
    string Type, Guid? ScopeId = null, IReadOnlyCollection<Guid>? StudentIds = null,
    string? Search = null, Guid? ClassId = null, Guid? SectionId = null, Guid? DepartmentId = null);

public sealed record BulkSmsRequest(
    string IdempotencyKey, BulkSmsScope Scope, string? Message = null, Guid? TemplateId = null,
    IReadOnlyDictionary<string, object?>? Variables = null);

public sealed record SmsRecipientPreview(Guid StudentId, string StudentName, string ParentName,
    string Phone, string Message);
public sealed record BulkSmsPreview(int MatchedStudents, int RecipientCount, int NoPhoneCount,
    int DuplicatePhoneCount, IReadOnlyList<SmsRecipientPreview> Examples,
    string PreviewToken, DateTimeOffset ExpiresAt);
public sealed record ApplyBulkSmsRequest(BulkSmsRequest Request, string PreviewToken);
public sealed record BulkSmsEnqueueResult(int QueuedCount, int ExistingCount, bool IdempotentReplay);
public sealed record SmsTargetStudent(Guid Id, string StudentNo, string Name);
public sealed record SmsTargetOption(Guid Id, string Name);
public sealed record SmsTargetOptions(IReadOnlyList<SmsTargetStudent> Students,
    IReadOnlyList<SmsTargetOption> Classes, IReadOnlyList<SmsTargetOption> Groups);

public interface ISmsLogRepository
{
    Task<SmsLogDetails> EnqueueAsync(string phone, string message, string idempotencyKey,
        Guid? studentId, Guid? templateId, CancellationToken cancellationToken);
    Task<PagedResult<SmsLogDetails>> ListAsync(SmsHistoryFilter filter, CancellationToken cancellationToken);
    Task<bool> RetryAsync(Guid id, CancellationToken cancellationToken);
}

public interface IBulkSmsRepository
{
    Task<SmsTargetOptions> TargetsAsync(string? search, CancellationToken cancellationToken);
    Task<IReadOnlyList<SmsRecipientSource>> ResolveAsync(BulkSmsScope scope, CancellationToken cancellationToken);
    Task<BulkSmsEnqueueResult> EnqueueAsync(IReadOnlyList<SmsRecipientPreview> recipients,
        Guid? templateId, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed record SmsRecipientSource(Guid StudentId, string StudentName, string? ParentName, string? Phone);

public sealed record SmsTemplateDetails(Guid Id, string Name, string Body, bool IsActive);

public sealed record SaveSmsTemplateRequest(string Name, string Body, bool IsActive = true);

public interface ISmsTemplateRepository
{
    Task<IReadOnlyList<SmsTemplateDetails>> ListAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<SmsTemplateDetails?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken);
    Task<SmsTemplateDetails> AddAsync(SaveSmsTemplateRequest request, CancellationToken cancellationToken);
    Task<SmsTemplateDetails?> UpdateAsync(Guid id, SaveSmsTemplateRequest request, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken);
}
