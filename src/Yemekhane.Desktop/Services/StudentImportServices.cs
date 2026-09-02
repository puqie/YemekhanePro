using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.StudentImports;

namespace Yemekhane.Desktop.Services;

public interface IStudentImportApiClient
{
    /// <summary>Dosyayi sunucuya yollar ve UYGULAMADAN once ne olacagini dondurur.</summary>
    Task<ImportPreviewResult> PreviewAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Onizlemede gorulen degisiklikleri uygular.</summary>
    Task<ImportApplyResult> ApplyAsync(ApplyStudentImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Hatali satirlarin raporunu diske indirir.</summary>
    Task DownloadErrorReportAsync(string token, string targetPath, CancellationToken cancellationToken = default);
}

public sealed class StudentImportApiClient(HttpClient client, IJwtSession session) : IStudentImportApiClient
{
    /// <summary>Sunucu 10 MB ustunu reddeder; kullaniciya dosyayi yollamadan once soylenir.</summary>
    public const long MaximumFileBytes = 10_000_000;

    public async Task<ImportPreviewResult> PreviewAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new FileNotFoundException("Seçilen dosya bulunamadı.", filePath);
        if (file.Length == 0) throw new InvalidDataException("Seçilen dosya boş.");
        if (file.Length > MaximumFileBytes)
            throw new InvalidDataException($"Dosya en fazla {MaximumFileBytes:N0} bayt olabilir; seçilen dosya {file.Length:N0} bayt.");

        using var request = Authorized(HttpMethod.Post, "api/imports/students/preview");
        // Akis kullanilir: 10 MB'lik dosyayi belleğe almanin gerekcesi yok.
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new MultipartFormDataContent();
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        // Alan adi "file" olmalidir: denetleyici parametresi IFormFile file.
        content.Add(fileContent, "file", file.Name);
        request.Content = content;
        return await SendAsync<ImportPreviewResult>(request, cancellationToken);
    }

    public async Task<ImportApplyResult> ApplyAsync(ApplyStudentImportRequest request,
        CancellationToken cancellationToken = default)
    {
        using var message = Authorized(HttpMethod.Post, "api/imports/students/apply");
        message.Content = JsonContent.Create(request);
        return await SendAsync<ImportApplyResult>(message, cancellationToken);
    }

    public async Task DownloadErrorReportAsync(string token, string targetPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        // Once gecici dosyaya yazilir: indirme yarida kalirsa kullanici yarim bir
        // raporu tam sanmamalidir.
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var request = Authorized(HttpMethod.Get, $"api/imports/students/{Uri.EscapeDataString(token)}/errors.csv");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await EnsureAsync(response, cancellationToken);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
            }

            File.Move(temporary, targetPath, true);
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("İçe aktarma API yanıtı boş döndü.");
    }

    /// <summary>
    /// Sunucunun reddi ("Zorunlu başlıklar eksik: NO, KART NO, AD, SOYAD." gibi) ProblemDetails
    /// basligiyla ApiRequestException olarak tasinir. Once HAM JSON govdesi HttpRequestException
    /// mesajina konuyordu; ViewModel bu tipi tanimadigi icin kullanici yalnizca genel
    /// "Dosya okunamadı" metnini goruyor, dosyada neyi duzeltecegini ogrenemiyordu.
    /// </summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode)
            throw await ApiErrors.ReadAsync(response, cancellationToken);
    }
}
