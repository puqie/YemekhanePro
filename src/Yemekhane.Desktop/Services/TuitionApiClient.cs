using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.Common;
using Yemekhane.Application.Statements;
using Yemekhane.Application.Tuition;

namespace Yemekhane.Desktop.Services;

/// <summary>Anasinifi ucret planlari ve ogrenci ekstresi icin API istemcisi.</summary>
public interface ITuitionApiClient
{
    Task<PagedResult<TuitionPlanDetails>> PlansAsync(TuitionPlanFilter filter, CancellationToken cancellationToken = default);
    Task<StudentTuitionSummary> ForStudentAsync(Guid studentId, CancellationToken cancellationToken = default);
    Task<TuitionPlanDetails> SavePlanAsync(SaveTuitionPlanRequest request, CancellationToken cancellationToken = default);
    Task DeletePlanAsync(Guid id, CancellationToken cancellationToken = default);
    Task<TuitionInstallmentDetails> ApplyPaymentAsync(ApplyTuitionPaymentRequest request, CancellationToken cancellationToken = default);
    Task<StudentStatement> StatementAsync(Guid studentId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default);
    /// <summary>Ekstreyi PDF olarak indirir; dosya hedef yola yazilir.</summary>
    Task DownloadStatementPdfAsync(Guid studentId, DateOnly startDate, DateOnly endDate, string path, CancellationToken cancellationToken = default);
}

public sealed class TuitionApiClient(HttpClient client, IJwtSession session) : ITuitionApiClient
{
    public Task<PagedResult<TuitionPlanDetails>> PlansAsync(TuitionPlanFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var values = new Dictionary<string, string?>
        {
            ["period"] = filter.Period,
            ["classId"] = filter.ClassId?.ToString("D"),
            ["studentId"] = filter.StudentId?.ToString("D"),
            ["includeInactive"] = filter.IncludeInactive ? "true" : null,
            ["page"] = filter.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = filter.PageSize.ToString(CultureInfo.InvariantCulture)
        };
        return GetAsync<PagedResult<TuitionPlanDetails>>("api/tuition/plans?" + Query(values), cancellationToken);
    }

    public Task<StudentTuitionSummary> ForStudentAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        GetAsync<StudentTuitionSummary>($"api/tuition/students/{studentId:D}", cancellationToken);

    public Task<TuitionPlanDetails> SavePlanAsync(SaveTuitionPlanRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TuitionPlanDetails>(HttpMethod.Post, "api/tuition/plans", request, cancellationToken);

    public async Task DeletePlanAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Delete, $"api/tuition/plans/{id:D}");
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    public Task<TuitionInstallmentDetails> ApplyPaymentAsync(ApplyTuitionPaymentRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<TuitionInstallmentDetails>(HttpMethod.Post, "api/tuition/payments", request, cancellationToken);

    public Task<StudentStatement> StatementAsync(Guid studentId, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) =>
        GetAsync<StudentStatement>($"api/students/{studentId:D}/statement?{Range(startDate, endDate)}", cancellationToken);

    public async Task DownloadStatementPdfAsync(Guid studentId, DateOnly startDate, DateOnly endDate, string path,
        CancellationToken cancellationToken = default)
    {
        using var request = Authorized(HttpMethod.Get, $"api/students/{studentId:D}/statement/pdf?{Range(startDate, endDate)}");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        // Once .tmp'ye yazilir: indirme yarida kalirsa kullanicinin elinde bozuk PDF kalmasin.
        var temporary = path + ".tmp";
        await using (var file = File.Create(temporary))
            await response.Content.CopyToAsync(file, cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private static string Range(DateOnly startDate, DateOnly endDate) =>
        $"from={startDate:yyyy-MM-dd}&to={endDate:yyyy-MM-dd}";

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, url);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Ücret planı API yanıtı boş döndü.");
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string url, object body, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, url);
        request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Ücret planı API yanıtı boş döndü.");
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    /// <summary>Sunucunun anlasilir hata mesajini (ProblemDetails) kullaniciya tasir; 5xx cevrimdisi akisina gider.</summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new LoginRequiredException();
        if (response.IsSuccessStatusCode) return;
        if ((int)response.StatusCode >= 500) response.EnsureSuccessStatusCode();
        throw await ApiErrors.ReadAsync(response, cancellationToken);
    }

    private static string Query(Dictionary<string, string?> values) =>
        string.Join('&', values.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
}

/// <summary>
/// Ekstre PDF'inin kaydedilecegi yolu sorar. Rapor ekranindaki secici rapor turune bagli
/// oldugu icin ekstre kendi arayuzunu kullanir; testte sahte uygulama verilir.
/// </summary>
public interface IStatementFileDialog
{
    /// <summary>Kullanici vazgecerse null doner.</summary>
    string? ChoosePdfPath(string suggestedFileName);
}

public sealed class StatementFileDialog : IStatementFileDialog
{
    public string? ChoosePdfPath(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedFileName,
            DefaultExt = ".pdf",
            Filter = "PDF dosyası (*.pdf)|*.pdf",
            Title = "Ekstreyi kaydet"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
