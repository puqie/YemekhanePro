using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Audit;

public sealed class EfAuditRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider) : IAuditRepository
{
    internal const int MaximumJsonBytes = 16 * 1024;
    private static readonly string[] SensitiveNames =
    [
        "nationalid", "tc", "phone", "telephone", "address", "password", "hash", "token", "devicekey",
        "secret", "securitystamp", "claimtoken", "cardnumber", "fingerprint", "photo"
    ];

    public void Add(AuditEntry entry, Guid? userId, string? correlationId)
    {
        dbContext.Set<AuditLog>().Add(new AuditLog
        {
            UserId = userId,
            Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
            Action = Required(entry.Action, 100),
            EntityName = Required(entry.EntityName, 100),
            EntityId = Limit(entry.EntityId, 128),
            Description = Required(entry.Description, 500),
            AffectedRecords = Math.Max(0, entry.AffectedRecords),
            BeforeJson = SerializeSafe(entry.Before),
            AfterJson = SerializeSafe(entry.After),
            BulkOperationId = entry.BulkOperationId,
            CorrelationId = Limit(correlationId, 128)
        });
    }

    public async Task<PagedResult<AuditLogDetails>> ListAsync(AuditLogFilter filter, CancellationToken cancellationToken)
    {
        var query = dbContext.Set<AuditLog>().AsNoTracking();
        if (filter.From.HasValue)
            query = query.Where(x => YemekhaneDbContext.JulianDay(x.Timestamp) >= YemekhaneDbContext.JulianDay(filter.From.Value));
        if (filter.To.HasValue)
            query = query.Where(x => YemekhaneDbContext.JulianDay(x.Timestamp) <= YemekhaneDbContext.JulianDay(filter.To.Value));
        if (filter.UserId.HasValue) query = query.Where(x => x.UserId == filter.UserId);
        if (!string.IsNullOrWhiteSpace(filter.Action)) query = query.Where(x => x.Action == filter.Action.Trim());
        if (!string.IsNullOrWhiteSpace(filter.Entity)) query = query.Where(x => x.EntityName == filter.Entity.Trim());
        if (!string.IsNullOrWhiteSpace(filter.EntityId)) query = query.Where(x => x.EntityId == filter.EntityId.Trim());
        if (filter.BulkOperationId.HasValue) query = query.Where(x => x.BulkOperationId == filter.BulkOperationId);
        if (!string.IsNullOrWhiteSpace(filter.CorrelationId)) query = query.Where(x => x.CorrelationId == filter.CorrelationId.Trim());
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(x => YemekhaneDbContext.JulianDay(x.Timestamp)).ThenByDescending(x => x.Id)
            .Skip((filter.Page - 1) * filter.PageSize).Take(filter.PageSize)
            .Select(x => new AuditLogDetails(x.Id, x.UserId, x.Timestamp, x.Action, x.EntityName, x.EntityId,
                x.Description, x.AffectedRecords, x.BeforeJson, x.AfterJson, x.BulkOperationId, x.CorrelationId))
            .ToListAsync(cancellationToken);
        return new PagedResult<AuditLogDetails>(items, filter.Page, filter.PageSize, total);
    }

    internal static string? SerializeSafe(object? value)
    {
        if (value is null) return null;
        var node = JsonSerializer.SerializeToNode(value);
        Redact(node);
        var json = node?.ToJsonString() ?? "null";
        if (Encoding.UTF8.GetByteCount(json) <= MaximumJsonBytes) return json;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
        return JsonSerializer.Serialize(new { truncated = true, originalBytes = Encoding.UTF8.GetByteCount(json), sha256 = hash });
    }

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj.ToList())
            {
                var normalized = new string(property.Key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                if (SensitiveNames.Any(normalized.Contains)) obj[property.Key] = "[REDACTED]";
                else Redact(property.Value);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array) Redact(child);
    }

    private static string Required(string value, int maximum) =>
        Limit(value?.Trim(), maximum) is { Length: > 0 } result ? result : throw new ArgumentException("Audit alanı boş olamaz.");
    private static string? Limit(string? value, int maximum) => value is null ? null : value[..Math.Min(value.Length, maximum)];
}

public sealed class SystemAuditContext : IAuditContext
{
    public Guid? UserId => null;
    public string? CorrelationId => null;
}
