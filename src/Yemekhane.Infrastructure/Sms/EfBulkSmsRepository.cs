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
        if (search is not null) students = ApplySearch(students, search);
        var studentItems = await students.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).Take(100)
            // Sinif ve sube, alici listesinde ayni isimli ogrencileri ayirt etmek icin
            // sart. LEFT JOIN semantigi icin iliskisel join yerine korelasyonlu alt
            // sorgu kullaniyoruz: Student.ClassId/SectionId NULLABLE oldugundan sinifi
            // atanmamis ogrencide FirstOrDefault() null doner ve satir DUSMEZ.
            // (Nullable anahtarla "join ... equals (Guid?)" kalibi EF'te kutulama
            // ceviri hatasina yol acabiliyor; alt sorgu bu tuzagi da atlar.)
            .Select(x => new SmsTargetStudent(x.Id, x.StudentNo, x.FirstName + " " + x.LastName,
                dbContext.Set<SchoolClass>().Where(c => c.Id == x.ClassId).Select(c => c.Name).FirstOrDefault(),
                dbContext.Set<Section>().Where(s => s.Id == x.SectionId).Select(s => s.Name).FirstOrDefault()))
            .ToListAsync(cancellationToken);
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
        // Birincil veli varsa o, yoksa herhangi bir aktif veli. Once yalnizca IsPrimary=true
        // araniyordu: velisi "birincil" isaretlenmeden kaydedilmis ogrenci (ogrenci ekrani
        // buna izin veriyor) onizlemede "TELEFON YOK" sayiliyor ve veliye SMS hic gitmiyordu.
        // Ogrenci listesi (EfStudentRepository) ayni kural ile telefon gosterdiginden
        // operator "telefon var ama SMS gitmedi" celiskisini goruyordu.
        return await query.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).Select(student =>
            new SmsRecipientSource(student.Id, student.FirstName + " " + student.LastName,
                dbContext.Parents.Where(parent => parent.StudentId == student.Id && parent.IsActive)
                    .OrderByDescending(parent => parent.IsPrimary).ThenBy(parent => parent.Id)
                    .Select(parent => parent.Name).FirstOrDefault(),
                dbContext.Parents.Where(parent => parent.StudentId == student.Id && parent.IsActive)
                    .OrderByDescending(parent => parent.IsPrimary).ThenBy(parent => parent.Id)
                    .Select(parent => parent.NormalizedPhone).FirstOrDefault())).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Ad/soyad aramasini Turkce kurallariyla, buyuk-kucuk harf duyarsiz yapar.
    /// </summary>
    /// <remarks>
    /// Once <c>FirstName.Contains(search)</c> kullaniliyordu; EF bunu SQLite'ta
    /// <c>instr()</c>'a cevirir ve instr HARF DUYARLIDIR. Sicil verisi buyuk harf
    /// ("ADA AKGÜN") tutuldugundan operatorun yazdigi "ada" HIC sonuc dondurmuyor,
    /// SMS alici listesi bos kaliyordu. Ogrenci ekrani ise SearchName sutunu ile
    /// buluyordu; iki ekran ayni ogrenciyi farkli goruyordu. SearchName, ad+soyadin
    /// TurkishSearchText ile normallestirilmis (buyuk harf, İ->I) halidir.
    /// </remarks>
    private static IQueryable<Student> ApplySearch(IQueryable<Student> query, string search)
    {
        var term = search.Trim();
        var normalized = TurkishSearchText.Normalize(term);
        return query.Where(x => x.StudentNo.Contains(term) || x.SearchName.Contains(normalized));
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
        if (!string.IsNullOrWhiteSpace(scope.Search)) query = ApplySearch(query, scope.Search);
        return query;
    }

    private static string Key(string batchKey, Guid studentId) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"sms-bulk:{batchKey}:{studentId:D}")));
}
