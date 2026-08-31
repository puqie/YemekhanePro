using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Yemekhane.Desktop.Services;

public sealed record DesktopLoginResult(string AccessToken, DateTimeOffset ExpiresAt);

public sealed class MutableJwtSession : IJwtSession
{
    public string? AccessToken { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public bool IsAuthenticated => !string.IsNullOrWhiteSpace(AccessToken) && ExpiresAt > DateTimeOffset.UtcNow;
    public void Set(string accessToken, DateTimeOffset expiresAt) { AccessToken = accessToken; ExpiresAt = expiresAt; }
}

public sealed class AuthenticationException(string message) : Exception(message);

public sealed class AuthenticationClient(HttpClient client, MutableJwtSession session)
{
    public MutableJwtSession Session { get; } = session;

    public async Task<DesktopLoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync("api/auth/login", new { Username = username, Password = password }, cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new AuthenticationException("Giriş isteği zaman aşımına uğradı. API bağlantısını kontrol edin."); }
        catch (HttpRequestException)
        { throw new AuthenticationException("Yerel API'ye ulaşılamadı. Bağlantıyı kontrol edip yeniden deneyin."); }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new AuthenticationException("Kullanıcı adı veya parola geçersiz. Tekrarlanan hatalı denemeler hesabı geçici olarak kilitleyebilir.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new AuthenticationException("Çok fazla giriş denemesi yapıldı. Kısa süre bekleyip yeniden deneyin.");
            if (!response.IsSuccessStatusCode)
                throw new AuthenticationException("Giriş servisi şu anda kullanılamıyor. Daha sonra yeniden deneyin.");
            // Bozuk/eksik govde ya da beklenmeyen icerik turu JsonException veya
            // NotSupportedException firlatir. Cevrilmezse bu, async void olan giris
            // isleyicisinde yakalanmadan kalir ve WPF uygulamayi mesaj gostermeden kapatir.
            try
            {
                return await response.Content.ReadFromJsonAsync<DesktopLoginResult>(cancellationToken: cancellationToken)
                    ?? throw new AuthenticationException("Giriş yanıtı okunamadı.");
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException)
            {
                throw new AuthenticationException("Giriş yanıtı anlaşılamadı. API sürümünü kontrol edin.");
            }
        }
    }
}
