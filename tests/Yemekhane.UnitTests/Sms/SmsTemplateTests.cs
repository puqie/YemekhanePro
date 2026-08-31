using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Common;
using Yemekhane.Application.Sms;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Infrastructure.Sms;
using Yemekhane.UnitTests.Api;

namespace Yemekhane.UnitTests.Sms;

public sealed class SmsTemplateTests
{
    [Fact]
    public async Task CrudAndDeactivateUsePersistentDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.MigrateAsync();
        var service = new SmsTemplateService(new EfSmsTemplateRepository(context));

        var created = await service.CreateAsync(new SaveSmsTemplateRequest(
            " Giriş Bildirimi ", " Sayın {{ParentName}}, {{StudentName}} saat {{EntryTime}} giriş yaptı. "));
        var updated = await service.UpdateAsync(created.Id, new SaveSmsTemplateRequest(
            "Giriş", "{{StudentName}} giriş yaptı."));
        var fetched = await service.GetAsync(created.Id);
        await service.DeactivateAsync(created.Id);

        Assert.Equal("Giriş", updated.Name);
        Assert.Equal(updated, fetched);
        Assert.Empty(await service.ListAsync());
        Assert.False(Assert.Single(await service.ListAsync(true)).IsActive);
    }

    [Fact]
    public async Task DuplicateNameIsRejected()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync();
        var service = new SmsTemplateService(new EfSmsTemplateRepository(context));
        await service.CreateAsync(new SaveSmsTemplateRequest("Bakiye", "Tutar: {{Amount}}"));

        await Assert.ThrowsAsync<EntityConflictException>(() =>
            service.CreateAsync(new SaveSmsTemplateRequest("Bakiye", "Yeni tutar: {{Amount}}")));
    }

    [Fact]
    public void RendererUsesTurkishFormatsDeterministically()
    {
        var result = SmsTemplateRenderer.Render(
            "Sayın {{ParentName}}, {{StudentName}} için {{ExpiryDate}} {{EntryTime}} {{Amount}} TL",
            new Dictionary<string, object?>
            {
                ["ParentName"] = "Ayşe Yılmaz",
                ["StudentName"] = "İpek Yılmaz",
                ["ExpiryDate"] = new DateOnly(2026, 9, 7),
                ["EntryTime"] = new TimeOnly(8, 5),
                ["Amount"] = 1234.5m
            });

        Assert.Equal("Sayın Ayşe Yılmaz, İpek Yılmaz için 07.09.2026 08:05 1.234,50 TL", result);
    }

    [Fact]
    public void RendererRejectsMissingAndUnknownVariables()
    {
        var missing = Assert.Throws<RequestValidationException>(() =>
            SmsTemplateRenderer.Render("Merhaba {{StudentName}}", new Dictionary<string, object?>()));
        var unknown = Assert.Throws<RequestValidationException>(() =>
            SmsTemplateRenderer.Render("Merhaba {{Password}}", new Dictionary<string, object?> { ["Password"] = "x" }));

        Assert.Contains("değer verilmelidir", missing.Message);
        Assert.Contains("Bilinmeyen", unknown.Message);
    }

    [Fact]
    public void RendererDoesNotRecursivelyEvaluateValues()
    {
        var result = SmsTemplateRenderer.Render("Merhaba {{ParentName}}", new Dictionary<string, object?>
        {
            ["ParentName"] = "{{StudentName}}",
            ["StudentName"] = "Çalıştırılmamalı"
        });

        Assert.Equal("Merhaba {{StudentName}}", result);
    }

    private static YemekhaneDbContext CreateContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<YemekhaneDbContext>().UseSqlite(connection).Options);
}

public sealed class SmsTemplateApiTests : IClassFixture<YemekhaneApiFactory>
{
    private readonly YemekhaneApiFactory factory;

    public SmsTemplateApiTests(YemekhaneApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task EndpointsUseAuthenticationAndPersistentCrud()
    {
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sms-templates")).StatusCode);

        using var client = factory.CreateOperatorClient();
        var name = $"Şablon-{Guid.NewGuid():N}";
        var createdResponse = await client.PostAsJsonAsync("/api/sms-templates", new
        {
            Name = name,
            Body = "Merhaba {{StudentName}}",
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetGuid();

        var fetched = await client.GetFromJsonAsync<JsonElement>($"/api/sms-templates/{id}");
        Assert.Equal(name, fetched.GetProperty("name").GetString());

        var duplicate = await client.PostAsJsonAsync("/api/sms-templates", new
        {
            Name = name,
            Body = "Başka metin",
            IsActive = true
        });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/sms-templates/{id}")).StatusCode);
        var active = await client.GetFromJsonAsync<JsonElement[]>("/api/sms-templates");
        var all = await client.GetFromJsonAsync<JsonElement[]>("/api/sms-templates?includeInactive=true");
        Assert.DoesNotContain(active!, item => item.GetProperty("id").GetGuid() == id);
        Assert.Contains(all!, item => item.GetProperty("id").GetGuid() == id && !item.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task InvalidTemplateReturnsValidationProblem()
    {
        using var client = factory.CreateOperatorClient();
        var response = await client.PostAsJsonAsync("/api/sms-templates", new
        {
            Name = $"Geçersiz-{Guid.NewGuid():N}",
            Body = "   ",
            IsActive = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
