using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using Yemekhane.Application.Reports;

namespace Yemekhane.Desktop.Services;

public enum ReportExportFormat { Pdf, Excel, Csv }

public interface IReportApiClient
{
    Task<ReportResult> QueryAsync(ReportType type, ReportQuery query, CancellationToken cancellationToken = default);
    Task ExportAsync(ReportType type, ReportQuery query, ReportExportFormat format, string targetPath,
        CancellationToken cancellationToken = default);
}

public sealed class ReportApiClient(HttpClient client, IJwtSession session) : IReportApiClient
{
    public async Task<ReportResult> QueryAsync(ReportType type, ReportQuery query,
        CancellationToken cancellationToken = default)
    {
        using var request = Authorized(BuildUrl(type, query));
        using var response = await client.SendAsync(request, cancellationToken);
        Ensure(response);
        return await response.Content.ReadFromJsonAsync<ReportResult>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Rapor API yanıtı boş döndü.");
    }

    public async Task ExportAsync(ReportType type, ReportQuery query, ReportExportFormat format, string targetPath,
        CancellationToken cancellationToken = default)
    {
        var suffix = format switch { ReportExportFormat.Pdf => "pdf", ReportExportFormat.Excel => "excel", _ => "csv" };
        var temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using var request = Authorized(BuildUrl(type, query, suffix, includePage: false));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            Ensure(response);
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

    public static string BuildUrl(ReportType type, ReportQuery query, string? suffix = null, bool includePage = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["start"] = query.Start?.ToString("O"), ["end"] = query.End?.ToString("O"),
            ["studentNo"] = query.StudentNo, ["cardNo"] = query.CardNo, ["firstName"] = query.FirstName,
            ["lastName"] = query.LastName, ["class"] = query.Class, ["department"] = query.Department,
            ["section"] = query.Section, ["job"] = query.Job, ["mealType"] = query.MealType,
            ["device"] = query.Device, ["decision"] = query.Decision, ["status"] = query.Status,
            ["sortBy"] = query.SortBy, ["descending"] = query.Descending.ToString().ToLowerInvariant()
        };
        if (includePage)
        {
            values["page"] = query.Page.ToString(CultureInfo.InvariantCulture);
            values["pageSize"] = query.PageSize.ToString(CultureInfo.InvariantCulture);
        }
        var path = $"api/reports/{type}" + (suffix is null ? "" : "/" + suffix);
        return path + "?" + string.Join("&", values.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value!)}"));
    }

    private HttpRequestMessage Authorized(string url)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return request;
    }

    private static void Ensure(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        response.EnsureSuccessStatusCode();
    }
}

public sealed record ReportColumnLayout(string Key, int DisplayIndex, double Width, bool IsVisible);

public interface IReportLayoutStore
{
    IReadOnlyList<ReportColumnLayout> Load(ReportType type);
    void Save(ReportType type, IReadOnlyList<ReportColumnLayout> columns);
}

public sealed class FileReportLayoutStore : IReportLayoutStore
{
    private readonly string path;
    public FileReportLayoutStore(string? path = null) => this.path = path ?? Path.Combine(
        Yemekhane.Infrastructure.Persistence.ApplicationDataPath.Resolve(), "report-layouts.json");

    public IReadOnlyList<ReportColumnLayout> Load(ReportType type)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var all = JsonSerializer.Deserialize<Dictionary<string, List<ReportColumnLayout>>>(File.ReadAllText(path));
            return all?.GetValueOrDefault(type.ToString()) ?? [];
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    public void Save(ReportType type, IReadOnlyList<ReportColumnLayout> columns)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        Dictionary<string, List<ReportColumnLayout>> all;
        try { all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, List<ReportColumnLayout>>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception ex) when (ex is JsonException or IOException) { all = []; }
        all[type.ToString()] = columns.ToList();
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all));
        File.Move(temporary, path, true);
    }
}

public interface IReportDialogService
{
    string? ChoosePath(ReportType type, ReportExportFormat format);
    void CopyText(string value);
}

public sealed class ReportDialogService : IReportDialogService
{
    public string? ChoosePath(ReportType type, ReportExportFormat format)
    {
        var (extension, filter) = format switch
        {
            ReportExportFormat.Pdf => ("pdf", "PDF belgesi (*.pdf)|*.pdf"),
            ReportExportFormat.Excel => ("xlsx", "Excel çalışma kitabı (*.xlsx)|*.xlsx"),
            _ => ("csv", "CSV dosyası (*.csv)|*.csv")
        };
        var dialog = new SaveFileDialog
        {
            AddExtension = true, DefaultExt = extension, Filter = filter,
            FileName = $"{type.ToString().ToLowerInvariant()}-{DateTime.Now:yyyyMMdd}"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void CopyText(string value) => Clipboard.SetText(value);
}
