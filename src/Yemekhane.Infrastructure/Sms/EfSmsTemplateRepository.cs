using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Audit;
using Yemekhane.Infrastructure.Audit;

namespace Yemekhane.Infrastructure.Sms;

public sealed class EfSmsTemplateRepository(YemekhaneDbContext dbContext, IAuditService auditService) : ISmsTemplateRepository
{
    public EfSmsTemplateRepository(YemekhaneDbContext dbContext)
        : this(dbContext, new AuditService(new EfAuditRepository(dbContext, TimeProvider.System), new SystemAuditContext())) { }
    public async Task<IReadOnlyList<SmsTemplateDetails>> ListAsync(bool includeInactive, CancellationToken cancellationToken) =>
        await dbContext.Set<SmsTemplate>().AsNoTracking()
            .Where(x => includeInactive || x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new SmsTemplateDetails(x.Id, x.Name, x.Body, x.IsActive))
            .ToListAsync(cancellationToken);

    public Task<SmsTemplateDetails?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Set<SmsTemplate>().AsNoTracking().Where(x => x.Id == id)
            .Select(x => new SmsTemplateDetails(x.Id, x.Name, x.Body, x.IsActive))
            .SingleOrDefaultAsync(cancellationToken);

    public Task<bool> NameExistsAsync(string name, Guid? excludingId, CancellationToken cancellationToken) =>
        dbContext.Set<SmsTemplate>().AnyAsync(
            x => x.Name == name && (!excludingId.HasValue || x.Id != excludingId), cancellationToken);

    public async Task<SmsTemplateDetails> AddAsync(SaveSmsTemplateRequest request, CancellationToken cancellationToken)
    {
        var template = new SmsTemplate { Name = request.Name, Body = request.Body, IsActive = request.IsActive };
        dbContext.Add(template);
        auditService.Record(new AuditEntry("SmsTemplateCreated", nameof(SmsTemplate), template.Id.ToString(), "SMS şablonu oluşturuldu.", After: template));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(template);
    }

    public async Task<SmsTemplateDetails?> UpdateAsync(
        Guid id, SaveSmsTemplateRequest request, CancellationToken cancellationToken)
    {
        var template = await dbContext.Set<SmsTemplate>().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (template is null) return null;
        var before = Map(template);
        template.Name = request.Name;
        template.Body = request.Body;
        template.IsActive = request.IsActive;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        auditService.Record(new AuditEntry("SmsTemplateUpdated", nameof(SmsTemplate), template.Id.ToString(), "SMS şablonu güncellendi.", Before: before, After: template));
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(template);
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var template = await dbContext.Set<SmsTemplate>()
            .SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken);
        if (template is null) return false;
        var before = Map(template);
        template.IsActive = false;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        auditService.Record(new AuditEntry("SmsTemplateDeactivated", nameof(SmsTemplate), template.Id.ToString(), "SMS şablonu pasifleştirildi.", Before: before, After: template));
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static SmsTemplateDetails Map(SmsTemplate template) =>
        new(template.Id, template.Name, template.Body, template.IsActive);
}
