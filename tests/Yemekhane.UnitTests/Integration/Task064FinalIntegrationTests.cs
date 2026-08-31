using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UglyToad.PdfPig;
using Yemekhane.Api.Devices;
using Yemekhane.Application.Access;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Application.Parents;
using Yemekhane.Application.Realtime;
using Yemekhane.Application.Reports;
using Yemekhane.Application.Students;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Simulators;
using Yemekhane.Devices.Turnstiles;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.UnitTests.Integration;

[Collection("LocalDatabase")]
public sealed class Task064FinalIntegrationTests
{
    private const string Username = "task064-admin";
    private const string Password = "Task064 secure bootstrap password!";
    private const string DeviceKey = "task064-device-key-0123456789";
    private const string SigningKey = "task064-signing-key-with-at-least-thirty-two-bytes";

    [Fact]
    [Trait("Category", "Task064")]
    public async Task RealFileApiSimulatorReportsAndRestartCompleteFinalAcceptanceSpine()
    {
        var directory = Path.Combine(Path.GetTempPath(), "YemekhanePro-Task064-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var databasePath = Path.Combine(directory, "task064.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        ScenarioState state;
        try
        {
            using (var factory = new FinalApiFactory(connectionString, bootstrap: true))
            {
                using var anonymous = factory.CreateClient();
                Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/students")).StatusCode);

                using var client = factory.CreateClient();
                using var loginResponse = await client.PostAsJsonAsync("/api/auth/login", new { Username, Password });
                Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
                var login = await ReadAsync<LoginDto>(loginResponse);
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);

                var schoolClass = await PostAsync<ClassRecord>(client, "/api/organization/classes", "12-A");
                var student = await PostAsync<StudentDetails>(client, "/api/students",
                    new SaveStudentRequest("064-001", "Final", "Öğrenci", ClassId: schoolClass.Id));
                _ = await PostAsync<ParentDetails>(client, $"/api/students/{student.Id}/parents",
                    new SaveParentRequest("Final Veli", "+905551110064", "Anne"));
                _ = await PostAsync<CardDetails>(client, $"/api/students/{student.Id}/cards",
                    new AssignCardRequest("CARD-064"));
                var meal = await PostAsync<MealTypeDetails>(client, "/api/meal-types",
                    new SaveMealTypeRequest("Öğle"));
                var today = IstanbulToday();
                var entitlement = await PostAsync<BulkEntitlementResult>(client, "/api/meal-entitlements/bulk",
                    new BulkEntitlementRequest([student.Id], meal.Id, today, today, Source: "Task064"));
                Assert.Equal(1, entitlement.CreatedCount);

                var device = await PostAsync<DeviceDto>(client, "/api/devices", new DeviceWriteRequest(
                    "TASK064-SIM", "Simulator", "Simulator", null, null, null, null,
                    IsActive: true, AutoConnect: false, HasTurnstile: true, "Kantin", "Entry"));
                var connected = await PostAsync<DeviceActionResult>(client, $"/api/devices/{device.Id}/connect", new { });
                Assert.True(connected.Succeeded);

                await using var reader = new SimulatorCardReader(device.Id, "TASK064-READER",
                    new DeviceEndpoint("Simulator"));
                await reader.ConnectAsync(CancellationToken.None);
                await using var cardEvents = reader.ReadCardsAsync(CancellationToken.None).GetAsyncEnumerator();
                reader.ScanCard("CARD-064");
                Assert.True(await cardEvents.MoveNextAsync());

                var firstOperation = Guid.NewGuid();
                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var turnstile = scope.ServiceProvider.GetRequiredService<TurnstileService>();
                    var first = await turnstile.ProcessCardReadAsync(new AccessCheckRequest(
                        cardEvents.Current.CardNumber, device.Id, meal.Id, IstanbulNoon(today), OperationId: firstOperation));
                    Assert.Equal("ALLOW", first.AccessDecision?.Decision);
                    Assert.Equal(HardwareCommandOutcome.Succeeded, first.HardwareOutcome);

                    reader.ScanCard("CARD-064");
                    Assert.True(await cardEvents.MoveNextAsync());
                    var second = await turnstile.ProcessCardReadAsync(new AccessCheckRequest(
                        cardEvents.Current.CardNumber, device.Id, meal.Id, IstanbulNoon(today).AddSeconds(1),
                        OperationId: Guid.NewGuid()));
                    Assert.Equal("DENY", second.AccessDecision?.Decision);
                    Assert.Contains("daha önce", second.AccessDecision!.Reason, StringComparison.OrdinalIgnoreCase);
                }

                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
                    Assert.Equal(1, await db.MealUsages.CountAsync(x => x.StudentId == student.Id));
                    var firstAccessLogId = await db.AccessLogs.Where(x => x.OperationId == firstOperation)
                        .Select(x => x.Id).SingleAsync();
                    Assert.Equal(1, await db.TurnstileEvents.CountAsync(x => x.AccessLogId == firstAccessLogId));
                    Assert.Contains(await db.SyncOperations.AsNoTracking().ToListAsync(),
                        x => x.OperationId == firstOperation && x.EntityName == "AccessLog");
                    Assert.NotEmpty(await db.AuditLogs.AsNoTracking().ToListAsync());
                }

                Assert.Contains(factory.Events.AccessDecisions, x => x.OperationId == firstOperation && x.Decision == "ALLOW");
                Assert.Contains(factory.Events.TurnstileResults, x => x.OperationId == firstOperation && x.Result == "SUCCEEDED");

                var report = await client.GetFromJsonAsync<ReportResult>("/api/reports/DailyAccess");
                Assert.NotNull(report);
                using var pdfResponse = await client.GetAsync("/api/reports/DailyAccess/pdf");
                using var excelResponse = await client.GetAsync("/api/reports/DailyAccess/excel");
                pdfResponse.EnsureSuccessStatusCode();
                excelResponse.EnsureSuccessStatusCode();
                var expectedTotal = report.Summary.TotalRecords;
                using (var pdf = PdfDocument.Open(await pdfResponse.Content.ReadAsByteArrayAsync()))
                {
                    var text = string.Join(" ", Enumerable.Range(1, pdf.NumberOfPages)
                        .SelectMany(page => pdf.GetPage(page).GetWords()).Select(word => word.Text));
                    Assert.Contains($"Toplam kayıt: {expectedTotal}", text, StringComparison.Ordinal);
                }
                using (var workbook = SpreadsheetDocument.Open(
                           new MemoryStream(await excelResponse.Content.ReadAsByteArrayAsync()), false))
                {
                    var totalRow = workbook.WorkbookPart!.WorksheetParts.Single().Worksheet!
                        .Descendants<Row>().Single(row => CellText(row.Elements<Cell>().First()) == "Toplam");
                    Assert.Equal(expectedTotal.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        totalRow.Elements<Cell>().ElementAt(1).CellValue!.Text);
                }

                var disconnected = await PostAsync<DeviceActionResult>(client, $"/api/devices/{device.Id}/disconnect", new { });
                Assert.True(disconnected.Succeeded);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/students")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
                Assert.True((await PostAsync<DeviceActionResult>(client, $"/api/devices/{device.Id}/reconnect", new { })).Succeeded);

                state = new ScenarioState(student.Id, firstOperation, expectedTotal);
            }

            SqliteConnection.ClearAllPools();
            using (var restarted = new FinalApiFactory(connectionString, bootstrap: false))
            {
                using var client = restarted.CreateClient();
                var login = await PostAsync<LoginDto>(client, "/api/auth/login", new { Username, Password });
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/students/{state.StudentId}")).StatusCode);
                await using var scope = restarted.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<YemekhaneDbContext>();
                Assert.Equal(1, await db.MealUsages.CountAsync(x => x.StudentId == state.StudentId));
                Assert.Equal(1, await db.AccessLogs.CountAsync(x => x.OperationId == state.OperationId));
                Assert.Equal(state.ReportTotal, await db.AccessLogs.CountAsync());
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path, object body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        return await ReadAsync<T>(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>()
        ?? throw new InvalidOperationException($"{typeof(T).Name} yanıtı boş.");

    private static string CellText(Cell cell) => cell.InlineString?.Text?.Text ?? cell.CellValue?.Text ?? string.Empty;

    private static DateOnly IstanbulToday()
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("Turkey Standard Time"); }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
    }

    private static DateTimeOffset IstanbulNoon(DateOnly date) =>
        new(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.FromHours(3));

    private sealed record LoginDto(string AccessToken, DateTimeOffset ExpiresAt);
    private sealed record ScenarioState(Guid StudentId, Guid OperationId, int ReportTotal);

    private sealed class FinalApiFactory(string connectionString, bool bootstrap) : WebApplicationFactory<Program>
    {
        public RecordingPublisher Events { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRealtimeEventPublisher>();
                services.AddSingleton<IRealtimeEventPublisher>(Events);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Database"] = connectionString,
                    ["Authentication:Jwt:SigningKey"] = SigningKey,
                    ["Authentication:Jwt:Issuer"] = "task064",
                    ["Authentication:Jwt:Audience"] = "task064",
                    ["Authentication:DeviceKeys:0"] = DeviceKey,
                    ["Authentication:Bootstrap:Enabled"] = bootstrap.ToString(),
                    ["Authentication:Bootstrap:Username"] = Username,
                    ["Authentication:Bootstrap:Password"] = Password,
                    ["Sync:Enabled"] = "true"
                }));
            return base.CreateHost(builder);
        }
    }

    private sealed class RecordingPublisher : IRealtimeEventPublisher
    {
        public List<AccessDecisionCommittedEvent> AccessDecisions { get; } = [];
        public List<TurnstileResultEvent> TurnstileResults { get; } = [];
        public List<DeviceStatusChangedEvent> DeviceStatuses { get; } = [];
        public List<NotificationEvent> Notifications { get; } = [];
        public ValueTask PublishAsync(AccessDecisionCommittedEvent value, CancellationToken cancellationToken = default)
        { AccessDecisions.Add(value); return ValueTask.CompletedTask; }
        public ValueTask PublishAsync(TurnstileResultEvent value, CancellationToken cancellationToken = default)
        { TurnstileResults.Add(value); return ValueTask.CompletedTask; }
        public ValueTask PublishAsync(DeviceStatusChangedEvent value, CancellationToken cancellationToken = default)
        { DeviceStatuses.Add(value); return ValueTask.CompletedTask; }
        public ValueTask PublishAsync(NotificationEvent value, CancellationToken cancellationToken = default)
        { Notifications.Add(value); return ValueTask.CompletedTask; }
    }
}
