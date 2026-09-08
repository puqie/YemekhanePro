using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Sms;

/// <summary>
/// Acilista varsayilan toplu SMS sablonlarini yazar (<see cref="DefaultSmsTemplates"/>).
/// Kurulum basina YALNIZCA BIR KEZ calisir: <see cref="SeededSettingKey"/> ayari yazildiktan
/// sonra bir daha dokunmaz; boylece kullanicinin sildigi/yeniden adlandirdigi varsayilan geri
/// gelmez. Ilk calismada tablo bos olmasa da eksik olanlari ekler (ad esitligi, buyuk/kucuk
/// harf duyarsiz): eski kurulumlarda okulun kendi sablonlari varken varsayilanlar hic
/// gelmiyordu, memur bunlari elle yaziyordu.
/// </summary>
public sealed class SmsTemplateSeeder(YemekhaneDbContext dbContext, TimeProvider timeProvider)
{
    public const string SeededSettingKey = "Sms.DefaultTemplatesSeeded";

    /// <summary>Eklenen sablon sayisi; daha once tohumlanmissa 0.</summary>
    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        var settings = dbContext.Set<SystemSetting>();
        if (await settings.AnyAsync(x => x.Key == SeededSettingKey, cancellationToken).ConfigureAwait(false))
            return 0;

        var templates = dbContext.Set<SmsTemplate>();
        var existingNames = new HashSet<string>(
            await templates.Select(x => x.Name).ToListAsync(cancellationToken).ConfigureAwait(false),
            StringComparer.OrdinalIgnoreCase);
        var now = timeProvider.GetUtcNow();
        var added = 0;
        foreach (var template in DefaultSmsTemplates.All)
        {
            if (!existingNames.Add(template.Name)) continue;
            templates.Add(new SmsTemplate
            {
                Name = template.Name,
                Body = SmsTemplateRenderer.ValidateTemplate(template.Body),
                IsActive = true,
                CreatedAt = now
            });
            added++;
        }
        settings.Add(new SystemSetting { Key = SeededSettingKey, Value = now.ToString("O"), CreatedAt = now });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }
}
