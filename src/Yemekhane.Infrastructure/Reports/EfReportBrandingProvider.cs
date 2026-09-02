using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Reports;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Reports;

/// <summary>
/// Rapor basligindaki kurum adini Ayarlar > Okul ekraninin kaydettigi "School.Name"
/// satirindan okur (bkz. SettingsService.Values). Satir yoksa ya da bossa null doner;
/// cagiran servis o zaman yapilandirmadaki adi kullanir.
/// </summary>
public sealed class EfReportBrandingProvider(YemekhaneDbContext db) : IReportBrandingProvider
{
    public const string SchoolNameKey = "School.Name";

    public async Task<string?> SchoolNameAsync(CancellationToken cancellationToken = default)
    {
        var value = await db.Set<SystemSetting>().AsNoTracking()
            .Where(x => x.Key == SchoolNameKey && !x.IsSecret)
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
