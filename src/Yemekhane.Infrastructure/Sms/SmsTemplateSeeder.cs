using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Sms;

/// <summary>
/// Acilista varsayilan toplu SMS sablonlarini yazar (<see cref="DefaultSmsTemplates"/>).
/// Yalnizca tablo TAMAMEN bosken calisir: pasif de olsa tek bir sablon varsa kullanici
/// listeyi zaten sekillendirmistir, ona dokunulmaz. Boylece hem ilk kurulum hem de hic
/// sablon acilmamis eski kurulumlar ayni listeyle baslar.
/// </summary>
public sealed class SmsTemplateSeeder(YemekhaneDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>Eklenen sablon sayisi; tablo bos degilse 0.</summary>
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await dbContext.Set<SmsTemplate>().AnyAsync(cancellationToken).ConfigureAwait(false))
            return 0;
        var now = timeProvider.GetUtcNow();
        foreach (var template in DefaultSmsTemplates.All)
        {
            dbContext.Add(new SmsTemplate
            {
                Name = template.Name,
                Body = SmsTemplateRenderer.ValidateTemplate(template.Body),
                IsActive = true,
                CreatedAt = now
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return DefaultSmsTemplates.All.Count;
    }
}
