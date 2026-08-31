using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sync;

namespace Yemekhane.Infrastructure.Sms;

public sealed class EfBulkSmsRepository(YemekhaneDbContext dbContext, TimeProvider timeProvider) : IBulkSmsRepository
{
    public async Task<SmsTargetOptions> TargetsAsync(string? search, CancellationToken cancellationToken)
    {
        var students = dbContext.Students.AsNoTracking().Where(x => x.IsActive);
        if (search is not null) students = students.Where(x => x.StudentNo.Contains(search) || x.FirstName.Contains(search) || x.LastName.Contains(search));
        var studentItems = await students.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).Take(100)
            .Select(x => new SmsTargetStudent(x.Id, x.StudentNo, x.FirstName + " " + x.LastName)).ToListAsync(cancellationToken);
        var classes = await dbContext.Set<SchoolClass>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new SmsTargetOption(x.Id, x.Name)).ToListAsync(cancellationToken);
        var groups = await dbContext.Set<StudentGroup>().AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new SmsTargetOption(x.Id, x.Name)).ToListAsync(cancellationToken);
        return new(studentItems, classes, groups);
    }

    public async Task<IReadOnlyList<SmsRecipientSource>> ResolveAsync(BulkSmsScope scope, CancellationToken cancellationToken)
    {
        var query = dbContext.Students.AsNoTracking().Where(x => x.IsActive);
        query = scope.Type switch
        {
            "Manual" => query.Where(x => scope.StudentIds!.Contains(x.Id)),
            "Class" => query.Where(x => x.ClassId == scope.ScopeId),
            "Group" => query.Where(x => dbContext.Set<StudentGroupMember>().Any(m => m.GroupId == scope.ScopeId && m.StudentId == x.Id)),
            "Filter" => ApplyFilter(query, scope),
            _ => query
        };
        return await query.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).Select(student =>
            new SmsRecipientSource(student.Id, student.FirstName + " " + student.LastName,
                dbContext.Parents.Where(parent => parent.StudentId == student.Id && parent.IsActive && parent.IsPrimary)
                    .Select(parent => parent.Name).FirstOrDefault(),
                dbContext.Parents.Where(parent => parent.StudentId == student.Id && parent.IsActive && parent.IsPrimary)
                    .Select(parent => parent.NormalizedPhone).FirstOrDefault())).ToListAsync(cancellationToken);
    }

    public async Task<BulkSmsEnqueueResult> EnqueueAsync(IReadOnlyList<SmsRecipientPreview> recipients,
        Guid? templateId, string idempotencyKey, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var existingCount = 0;
        foreach (var recipient in recipients)
        {
            var key = Key(idempotencyKey, recipient.StudentId);
            var existing = await dbContext.SmsLogs.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
            if (existing is not null)
            {
                if (existing.StudentId != recipient.StudentId || existing.Phone != recipient.Phone ||
                    existing.Message != recipient.Message || existing.TemplateId != templateId)
                    throw new EntityConflictException("IdempotencyKey daha önce farklı bir SMS gönderimi için kullanıldı.");
                existingCount++;
                continue;
            }
            var sms = new SmsLog
            {
                StudentId = recipient.StudentId, TemplateId = templateId, Phone = recipient.Phone,
                Message = recipient.Message, Status = SmsLogStatuses.Pending, IdempotencyKey = key,
                CreatedAt = now, NextAttemptAt = now
            };
            dbContext.SmsLogs.Add(sms);
            LocalOutbox.Enqueue(dbContext, sms, LocalOutbox.QueueSms, sms, timestamp: now);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(recipients.Count - existingCount, existingCount, existingCount == recipients.Count && recipients.Count > 0);
    }

    private static IQueryable<Student> ApplyFilter(IQueryable<Student> query, BulkSmsScope scope)
    {
        if (scope.ClassId.HasValue) query = query.Where(x => x.ClassId == scope.ClassId);
        if (scope.SectionId.HasValue) query = query.Where(x => x.SectionId == scope.SectionId);
        if (scope.DepartmentId.HasValue) query = query.Where(x => x.DepartmentId == scope.DepartmentId);
        if (!string.IsNullOrWhiteSpace(scope.Search))
        {
            var search = scope.Search.Trim();
            query = query.Where(x => x.StudentNo.Contains(search) || x.FirstName.Contains(search) || x.LastName.Contains(search));
        }
        return query;
    }

    private static string Key(string batchKey, Guid studentId) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"sms-bulk:{batchKey}:{studentId:D}")));
}
