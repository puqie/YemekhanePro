using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// Yolculuk testlerinin ekranda gordugunu API'den BAGIMSIZ dogrulamasi icin ince yardimci:
/// ViewModel "kaydedildi" dese bile sunucuya gercekten ne yazildigi buradan okunur.
/// STA icinde .Result kilitlenecegi icin bekleme LiveUiHarness.Wait ile yapilir.
/// </summary>
internal static class LiveApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static T Get<T>(LiveUiHarness ui, string url) =>
        Send<T>(ui, HttpMethod.Get, url, null);

    public static T Post<T>(LiveUiHarness ui, string url, object body) =>
        Send<T>(ui, HttpMethod.Post, url, body);

    public static HttpResponseMessage Delete(LiveUiHarness ui, string url) =>
        SendRaw(ui, HttpMethod.Delete, url, null);

    /// <summary>Yalnizca durum kodu: silinmis kaydin 404 dondugunu dogrulamak icin.</summary>
    public static System.Net.HttpStatusCode StatusOf(LiveUiHarness ui, string url)
    {
        using var response = SendRaw(ui, HttpMethod.Get, url, null);
        return response.StatusCode;
    }

    private static T Send<T>(LiveUiHarness ui, HttpMethod method, string url, object? body)
    {
        using var response = SendRaw(ui, method, url, body);
        var read = response.Content.ReadAsStringAsync();
        Assert.True(LiveUiHarness.Wait(read, TimeSpan.FromSeconds(20)), "API gövdesi okunamadı: " + url);
        Assert.True(response.IsSuccessStatusCode, $"{method} {url} -> {(int)response.StatusCode}: {read.Result}");
        return JsonSerializer.Deserialize<T>(read.Result, Json)!;
    }

    private static HttpResponseMessage SendRaw(LiveUiHarness ui, HttpMethod method, string url, object? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ui.Session.AccessToken);
        if (body is not null) request.Content = JsonContent.Create(body);
        var send = ui.Http.SendAsync(request);
        Assert.True(LiveUiHarness.Wait(send, TimeSpan.FromSeconds(30)), "API isteği zaman aşımına uğradı: " + url);
        return send.Result;
    }
}
