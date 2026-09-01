using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Yemekhane.Licensing;

/// <summary>
/// Aktivasyon sunucusuyla HTTP uzerinden konusur.
///
/// Sunucu sozlesmesi (tasarim dokumaniyla ayni):
///   POST /activate  200 basarili | 404 anahtar yok | 409 baska makinede | 410 iptal
///   POST /validate  200 gecerli  | 410 iptal
///
/// Sunucu henuz yoktur; bu sinif sozlesmeye gore yazilmistir ve sunucu hazir
/// oldugunda DEGISMESI gerekmez.
/// </summary>
public sealed class HttpLicenseActivationClient : ILicenseActivationClient
{
    private readonly HttpClient httpClient;

    public HttpLicenseActivationClient(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
    }

    public async Task<ActivationResult> ActivateAsync(
        string licenseKey, HardwareFingerprint fingerprint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync("activate",
                    new ActivationRequest(licenseKey, [.. fingerprint.Hashes]), cancellationToken)
                .ConfigureAwait(false);

            // Her durum koduna KENDI Turkce aciklamasi verilir: kullanici anahtarini
            // mi yanlis yazdigini, yoksa lisansin baska bir bilgisayarda mi oldugunu
            // bilmelidir. Tek bir "hata olustu" mesaji destege gereksiz cagri yaratir.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(false, null, "Lisans anahtari bulunamadi. Lutfen anahtari kontrol edin.");

            if (response.StatusCode == HttpStatusCode.Conflict)
                return new(false, null,
                    "Bu lisans baska bir bilgisayarda kullaniliyor. Yeni bilgisayara tasimak icin satici ile iletisime gecin.");

            if (response.StatusCode == HttpStatusCode.Gone)
                return new(false, null, "Bu lisans iptal edilmis. Lutfen satici ile iletisime gecin.");

            if (!response.IsSuccessStatusCode)
                return new(false, null, "Lisans sunucusu su anda yanit vermiyor. Lutfen daha sonra tekrar deneyin.");

            var payload = await response.Content
                .ReadFromJsonAsync<ActivationResponse>(cancellationToken)
                .ConfigureAwait(false);

            if (payload is null)
                return new(false, null, "Lisans sunucusundan beklenmeyen bir yanit alindi.");

            return new(true, new StoredLicense(
                licenseKey.Trim(),
                payload.CustomerName ?? string.Empty,
                payload.Edition ?? string.Empty,
                [.. fingerprint.Hashes],
                payload.IssuedAt,
                payload.ExpiresAt,
                payload.IssuedAt,
                payload.Signature ?? string.Empty), null);
        }
        catch (HttpRequestException)
        {
            return new(false, null,
                "Lisans sunucusuna baglanilamadi. Internet baglantinizi kontrol edip tekrar deneyin.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null, "Lisans sunucusu zaman asimina ugradi. Lutfen tekrar deneyin.");
        }
    }

    public async Task<ValidationResult> ValidateAsync(
        StoredLicense license, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(license);

        try
        {
            using var response = await httpClient
                .PostAsJsonAsync("validate",
                    new ValidationRequest(license.LicenseKey, [.. license.FingerprintHashes], license.Signature),
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Gone)
                return new(ValidationOutcome.Revoked);

            // Sunucu hatalari (500, 503) IPTAL SAYILMAZ: bizim tarafimizdaki bir ariza
            // yuzunden sahadaki tum okullari kilitlemek kabul edilemez.
            if (!response.IsSuccessStatusCode)
                return new(ValidationOutcome.Unreachable);

            var payload = await response.Content
                .ReadFromJsonAsync<ValidationResponse>(cancellationToken)
                .ConfigureAwait(false);

            return payload is null
                ? new(ValidationOutcome.Unreachable)
                : new(ValidationOutcome.Valid, payload.ExpiresAt, payload.Signature);
        }
        catch (HttpRequestException)
        {
            return new(ValidationOutcome.Unreachable);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ValidationOutcome.Unreachable);
        }
    }

    private sealed record ActivationRequest(
        [property: JsonPropertyName("licenseKey")] string LicenseKey,
        [property: JsonPropertyName("fingerprints")] string[] Fingerprints);

    private sealed record ValidationRequest(
        [property: JsonPropertyName("licenseKey")] string LicenseKey,
        [property: JsonPropertyName("fingerprints")] string[] Fingerprints,
        [property: JsonPropertyName("signature")] string Signature);

    private sealed record ActivationResponse(
        [property: JsonPropertyName("customerName")] string? CustomerName,
        [property: JsonPropertyName("edition")] string? Edition,
        [property: JsonPropertyName("issuedAt")] DateTimeOffset IssuedAt,
        [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("signature")] string? Signature);

    private sealed record ValidationResponse(
        [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt,
        [property: JsonPropertyName("signature")] string? Signature);
}
