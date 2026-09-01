using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Yemekhane.UnitTests.Api;

/// <summary>
/// Arayüz-backend sözleşmesini koruyan testler. Masaüstü istemci bu uçlara bağlandığında
/// kırılmaların burada yakalanması hedeflenir.
/// </summary>
public sealed class ApiContractTests : IClassFixture<YemekhaneApiFactory>
{
    private readonly YemekhaneApiFactory factory;

    public ApiContractTests(YemekhaneApiFactory factory) => this.factory = factory;

    public static TheoryData<string, string> OperatorEndpoints => new()
    {
        { "GET", "/api/students" },
        { "GET", "/api/meal-types" },
        { "GET", "/api/holidays?startsOn=2026-01-01&endsOn=2026-12-31" },
        { "GET", "/api/organization/classes" },
        { "GET", "/api/organization/groups" },
        { "GET", "/api/sms" },
        { "GET", "/api/income/transactions" },
        { "GET", "/api/cash/summary?period=Daily&date=2026-08-31" },
        { "GET", "/api/reports/DailyAccess" },
        { "GET", "/api/daily-tracking?pageSize=10" },
    };

    [Theory]
    [MemberData(nameof(OperatorEndpoints))]
    public async Task OperatorEndpointsRequireAuthentication(string method, string path)
    {
        using var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(OperatorEndpoints))]
    public async Task OperatorEndpointsAnswerAuthenticatedCaller(string method, string path)
    {
        using var client = factory.CreateOperatorClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,
            $"{method} {path} -> {(int)response.StatusCode} {response.StatusCode}: {responseBody}");
    }

    [Fact]
    public async Task OperatorTokenIsRejectedOnDeviceEndpoint()
    {
        using var client = factory.CreateOperatorClient();

        var response = await client.PostAsJsonAsync("/api/access/check", new
        {
            CardNumber = "0001",
            DeviceId = Guid.NewGuid(),
            MealTypeId = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidationFailureReturnsProblemDetailsWithBadRequest()
    {
        using var client = factory.CreateOperatorClient();

        var response = await client.PostAsJsonAsync("/api/students", new
        {
            StudentNo = "",
            FirstName = "",
            LastName = ""
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
    }

    [Fact]
    public async Task MissingEntityReturnsNotFound()
    {
        using var client = factory.CreateOperatorClient();

        var response = await client.GetAsync($"/api/students/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task StudentSurvivesCreateThenReadRoundTrip()
    {
        using var client = factory.CreateOperatorClient();
        var studentNo = $"T{Guid.NewGuid():N}"[..12];

        var created = await client.PostAsJsonAsync("/api/students", new
        {
            StudentNo = studentNo,
            FirstName = "Zeynep",
            LastName = "Kaya"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        var fetched = await client.GetFromJsonAsync<JsonElement>($"/api/students/{id}");

        Assert.Equal(studentNo, fetched.GetProperty("studentNo").GetString());
        Assert.Equal("Zeynep", fetched.GetProperty("firstName").GetString());
    }

    [Fact]
    public async Task DuplicateStudentNoReturnsConflict()
    {
        using var client = factory.CreateOperatorClient();
        var studentNo = $"D{Guid.NewGuid():N}"[..12];
        var payload = new { StudentNo = studentNo, FirstName = "Ali", LastName = "Vural" };

        var first = await client.PostAsJsonAsync("/api/students", payload);
        var second = await client.PostAsJsonAsync("/api/students", payload);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task CashSummaryReturnsMoneyAsJsonNumber()
    {
        using var client = factory.CreateOperatorClient();

        var response = await client.GetAsync("/api/cash/daily?date=2026-08-31");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Number, body.GetProperty("totalAmount").ValueKind);
        Assert.Equal(JsonValueKind.Number, body.GetProperty("voidedAmount").ValueKind);
    }
}
