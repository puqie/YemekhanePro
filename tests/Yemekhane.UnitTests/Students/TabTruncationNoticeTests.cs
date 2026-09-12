using System.Net;
using System.Net.Http;
using System.Text;
using Yemekhane.Desktop.Services;

namespace Yemekhane.UnitTests.Students;

/// <summary>
/// Sunucu sayfa basina en fazla 200 kayit doner. Masaustu bunu SOYLEMEZSE kullanici
/// 200 satiri gorup listenin BITTIGINI sanir.
///
/// Bu, "gecen yila bakayim" diye acilan bir ekranda aradigi kaydin "yok" gorunmesi
/// demektir: en tehlikeli hata turu, cunku kullanici yanildigini hic anlamaz.
///
/// Iki bicim kontrol edilir: gecis gecmisi "hasMore" bayragi, sayfali uclar
/// (Odemeler, SMS, Denetim) "totalCount" doner.
/// </summary>
public sealed class TabTruncationNoticeTests
{
    private const string Notice = "Daha fazla kayıt var";

    private static StudentApiClient Client(string json)
    {
        var http = new HttpClient(new StubHandler(json)) { BaseAddress = new Uri("http://localhost/") };
        return new StudentApiClient(http, new StubSession());
    }

    private static async Task<IReadOnlyList<string>> RowsAsync(string tab, string json)
    {
        var rows = await Client(json).LoadTabAsync(tab, Guid.NewGuid());
        return [.. rows.Select(x => x.ToString() ?? "")];
    }

    /// <summary>hasMore=true ise UYARI SATIRI eklenir.</summary>
    [Fact]
    public async Task AccessHistoryWarnsWhenTheServerSaysThereIsMore()
    {
        var rows = await RowsAsync("Access History",
            """{"items":[{"timestamp":"2026-09-12T12:00:00+03:00","decision":"ALLOW"}],"hasMore":true}""");

        Assert.Contains(rows, x => x.Contains(Notice, StringComparison.Ordinal));
    }

    /// <summary>hasMore=false ise uyari CIKMAZ; gereksiz uyari da kullaniciyi yorar.</summary>
    [Fact]
    public async Task AccessHistoryStaysQuietWhenEverythingFits()
    {
        var rows = await RowsAsync("Access History",
            """{"items":[{"timestamp":"2026-09-12T12:00:00+03:00","decision":"ALLOW"}],"hasMore":false}""");

        Assert.DoesNotContain(rows, x => x.Contains(Notice, StringComparison.Ordinal));
    }

    /// <summary>Sayfali uclarda totalCount satir sayisindan BUYUKSE uyarilir.</summary>
    [Fact]
    public async Task PagedTabsWarnWhenTheTotalExceedsWhatArrived()
    {
        var rows = await RowsAsync("Payments",
            """{"items":[{"occurredAt":"2026-09-12T12:00:00+03:00","amount":100}],"totalCount":250}""");

        Assert.Contains(rows, x => x.Contains(Notice, StringComparison.Ordinal));
    }

    /// <summary>totalCount gelen satir sayisina esitse uyari cikmaz.</summary>
    [Fact]
    public async Task PagedTabsStayQuietWhenTheTotalMatches()
    {
        var rows = await RowsAsync("Payments",
            """{"items":[{"occurredAt":"2026-09-12T12:00:00+03:00","amount":100}],"totalCount":1}""");

        Assert.DoesNotContain(rows, x => x.Contains(Notice, StringComparison.Ordinal));
    }

    /// <summary>Uyari LISTENIN SONUNA eklenir; gercek kayitlarin onune gecmemeli.</summary>
    [Fact]
    public async Task TheNoticeIsAppendedAfterTheRealRows()
    {
        var rows = await RowsAsync("Access History",
            """{"items":[{"timestamp":"2026-09-12T12:00:00+03:00","decision":"ALLOW"}],"hasMore":true}""");

        Assert.Equal(2, rows.Count);
        Assert.Contains(Notice, rows[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// DUZ LISTE donen sekmeler (Kartlar, Veliler, Hakedisler, Izinler, Tatil/Aktarim)
    /// kesilme kontrolunden ETKILENMEMELI. Olculdu: "hasMore" aramasi listenin kokunde
    /// nesne alani aradigi icin InvalidOperationException atiyor, bes sekme birden
    /// "Sekme verisi alınamadı: The requested operation requires an element of type
    /// 'Object'..." diyordu. Sahada Kartlar sekmesi bu hatayla acilmiyordu.
    /// </summary>
    [Theory]
    [InlineData("Cards", """[{"cardNumber":"8247129","isActive":true}]""")]
    [InlineData("Parents", """[{"fullName":"Veli Veli","phone":"05551112233"}]""")]
    [InlineData("Entitlements", """[{"entitlementDate":"2026-09-12","quantity":1,"status":"Active"}]""")]
    [InlineData("Leaves", """[{"startsOn":"2026-09-12","endsOn":"2026-09-12","leaveType":"Sağlık"}]""")]
    [InlineData("Holiday/Transfer", """[{"transferDate":"2026-09-12","status":"Transferred"}]""")]
    public async Task ArrayRootedTabsLoadWithoutTheTruncationCheckBreakingThem(string tab, string json)
    {
        var rows = await RowsAsync(tab, json);

        Assert.Single(rows);
        Assert.DoesNotContain(rows, x => x.Contains(Notice, StringComparison.Ordinal));
    }

    /// <summary>Bos liste de sorunsuz: "kayit yok" durumu hata gibi gorunmemeli.</summary>
    [Fact]
    public async Task AnEmptyArrayRootedTabYieldsNoRowsAndNoError()
    {
        var rows = await RowsAsync("Cards", "[]");

        Assert.Empty(rows);
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class StubSession : IJwtSession
    {
        public string? AccessToken => "token";
        public bool IsAuthenticated => true;
    }
}
