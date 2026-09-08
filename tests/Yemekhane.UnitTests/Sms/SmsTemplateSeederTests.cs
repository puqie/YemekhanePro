using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Sms;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sms;

namespace Yemekhane.UnitTests.Sms;

/// <summary>
/// Toplu SMS ekrani ilk acilista bos sablon listesiyle geliyordu. Varsayilanlar yalnizca
/// tablo tamamen bosken tohumlanir; kullanicinin sildigi (pasif) sablon bile "liste
/// sekillendirilmis" sayilir ve hicbir sey eklenmez.
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

    [Fact]
    public async Task UserCreatedTemplateBlocksSeeding()
    {
        db.Add(new SmsTemplate { Name = "Okulun kendi şablonu", Body = "Merhaba {{ParentName}}" });
        await db.SaveChangesAsync();

        var added = await Seeder().SeedAsync();

        Assert.Equal(0, added);
        Assert.Equal(1, await db.Set<SmsTemplate>().CountAsync());
    }

    /// <summary>Silinen (pasif) sablon da "liste kullanicinin" demektir; geri gelmez.</summary>
    [Fact]
    public async Task DeactivatedOnlyTableStillBlocksSeeding()
    {
        db.Add(new SmsTemplate { Name = "Silinmiş", Body = "Merhaba", IsActive = false });
        await db.SaveChangesAsync();

        Assert.Equal(0, await Seeder().SeedAsync());
        Assert.Equal(1, await db.Set<SmsTemplate>().CountAsync());
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
