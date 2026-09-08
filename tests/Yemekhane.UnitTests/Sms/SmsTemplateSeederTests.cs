using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

/// <summary>
/// Toplu SMS ekrani ilk acilista bos sablon listesiyle geliyordu. Varsayilanlar kurulum
/// basina bir kez tohumlanir (SystemSetting bayragi); eksik adlar eklenir, sonraki
/// calismalarda kullanicinin sildigi sablon geri gelmez.
/// </summary>
public sealed class SmsTemplateSeederTests : IAsyncDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly YemekhaneDbContext db;

    public SmsTemplateSeederTests()
    {
        connection.Open();
        db = new YemekhaneDbContext(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() { await db.DisposeAsync(); await connection.DisposeAsync(); }

    private SmsTemplateSeeder Seeder() => new(db, TimeProvider.System);

    [Fact]
    public async Task EmptyTableGetsEveryDefaultTemplateActive()
    {
        var added = await Seeder().SeedAsync();

        var rows = await db.Set<SmsTemplate>().OrderBy(x => x.Name).ToListAsync();
        Assert.Equal(DefaultSmsTemplates.All.Count, added);
        Assert.Equal(DefaultSmsTemplates.All.Count, rows.Count);
        Assert.All(rows, row => Assert.True(row.IsActive));
        Assert.Contains(rows, row => row.Name == "Yemek Ücreti Hatırlatma");
        Assert.Contains(rows, row => row.Body.Contains("{{ParentName}}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SecondRunAddsNothing()
    {
        await Seeder().SeedAsync();

        var added = await Seeder().SeedAsync();

        Assert.Equal(0, added);
        Assert.Equal(DefaultSmsTemplates.All.Count, await db.Set<SmsTemplate>().CountAsync());
    }

    /// <summary>Eski kurulum: okulun kendi sablonu varken varsayilanlar da eklenir, kullanicininki kalir.</summary>
    [Fact]
    public async Task ExistingUserTemplateIsKeptAndDefaultsAreAddedBesideIt()
    {
        db.Add(new SmsTemplate { Name = "Okulun kendi şablonu", Body = "Merhaba {{ParentName}}" });
        await db.SaveChangesAsync();

        var added = await Seeder().SeedAsync();

        Assert.Equal(DefaultSmsTemplates.All.Count, added);
        Assert.Equal(DefaultSmsTemplates.All.Count + 1, await db.Set<SmsTemplate>().CountAsync());
        Assert.Contains(await db.Set<SmsTemplate>().ToListAsync(), x => x.Name == "Okulun kendi şablonu");
    }

    /// <summary>Ayni adli sablon (buyuk/kucuk harf farkli, pasif bile olsa) varsa o varsayilan atlanir; kopya olusmaz.</summary>
    [Fact]
    public async Task SameNamedTemplateIsNotDuplicated()
    {
        db.Add(new SmsTemplate { Name = "yemek ücreti hatırlatma", Body = "Kendi metnim {{ParentName}}", IsActive = false });
        await db.SaveChangesAsync();

        var added = await Seeder().SeedAsync();

        Assert.Equal(DefaultSmsTemplates.All.Count - 1, added);
        var rows = (await db.Set<SmsTemplate>().ToListAsync()).Where(x => string.Equals(x.Name, "Yemek Ücreti Hatırlatma", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(rows);
        Assert.Equal("Kendi metnim {{ParentName}}", rows[0].Body);
    }

    /// <summary>Tohumlama bir kez yapilir: kullanici varsayilani silerse (pasif) veya yeniden adlandirirsa geri gelmez.</summary>
    [Fact]
    public async Task DeletedDefaultDoesNotComeBackOnLaterRuns()
    {
        await Seeder().SeedAsync();
        var row = await db.Set<SmsTemplate>().SingleAsync(x => x.Name == "Kart Yenilendi");
        db.Remove(row);
        await db.SaveChangesAsync();

        var added = await Seeder().SeedAsync();

        Assert.Equal(0, added);
        Assert.False(await db.Set<SmsTemplate>().AnyAsync(x => x.Name == "Kart Yenilendi"));
        Assert.True(await db.Set<SystemSetting>().AnyAsync(x => x.Key == SmsTemplateSeeder.SeededSettingKey));
    }

    /// <summary>Her varsayilan, toplu gonderimin izin verdigi degiskenlerle ve tekil adla yazilmis olmali.</summary>
    [Fact]
    public void DefaultsAreValidTemplatesWithUniqueNames()
    {
        Assert.All(DefaultSmsTemplates.All, template =>
        {
            Assert.InRange(template.Name.Length, 2, 100);
            Assert.Equal(template.Body, SmsTemplateRenderer.ValidateTemplate(template.Body));
            Assert.Contains("{{ParentName}}", template.Body, StringComparison.Ordinal);
        });
        Assert.Equal(DefaultSmsTemplates.All.Count, DefaultSmsTemplates.All.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Tohumlayici DI'da ve acilis zincirinde bagli olmali; yoksa ozellik "yazilmis" gorunur, sahada calismaz.</summary>
    [Fact]
    public void SeederIsWiredIntoStartup()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Yemekhane.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var program = File.ReadAllText(Path.Combine(root!.FullName, "src", "Yemekhane.Api", "Program.cs"));
        var registration = File.ReadAllText(Path.Combine(root.FullName, "src", "Yemekhane.Infrastructure", "Sms", "SmsRegistration.cs"));

        Assert.Contains("GetRequiredService<SmsTemplateSeeder>().SeedAsync(", program, StringComparison.Ordinal);
        Assert.Contains("AddScoped<SmsTemplateSeeder>()", registration, StringComparison.Ordinal);
    }
}
