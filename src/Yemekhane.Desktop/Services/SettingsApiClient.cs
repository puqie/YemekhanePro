using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Yemekhane.Application.Common;
using Yemekhane.Application.Settings;
using Yemekhane.Application.Sms;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Sync;

namespace Yemekhane.Desktop.Services;

public interface ISettingsApiClient
{
    Task<SettingsDocument> GetAsync(CancellationToken cancellationToken = default);
    Task<SaveSettingsResult> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default);
    Task<BackupCommandResult> BackupNowAsync(CancellationToken cancellationToken = default);
    Task<BackupValidationResult> ValidateBackupAsync(string path, CancellationToken cancellationToken = default);
    Task<RestoreResult> RestoreAsync(string path, string confirmation, CancellationToken cancellationToken = default);
    Task<SyncRunResult> RunSyncAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<SyncConflictItem>> SyncConflictsAsync(CancellationToken cancellationToken = default);
    Task RequeueConflictAsync(Guid operationId, CancellationToken cancellationToken = default);
    Task<PagedResult<ApplicationLogItem>> LogsAsync(int page, int pageSize, CancellationToken cancellationToken = default);
    Task<SmsAutomationStatus> GetSmsAutomationAsync(CancellationToken cancellationToken = default);
    Task<SmsAutomationStatus> SaveSmsAutomationAsync(SmsAutomationSettings settings, CancellationToken cancellationToken = default);
    Task<EntitlementWarningRunResult> RunEntitlementWarningAsync(CancellationToken cancellationToken = default);
}

public sealed class SettingsApiClient(HttpClient client, IJwtSession session) : ISettingsApiClient
{
    public Task<SettingsDocument> GetAsync(CancellationToken cancellationToken = default) =>
        SendAsync<SettingsDocument>(HttpMethod.Get, "api/settings", null, cancellationToken);
    public Task<SaveSettingsResult> SaveAsync(SaveSettingsRequest request, CancellationToken cancellationToken = default) =>
        SendAsync<SaveSettingsResult>(HttpMethod.Put, "api/settings", JsonContent.Create(request), cancellationToken);
    public Task<BackupCommandResult> BackupNowAsync(CancellationToken cancellationToken = default) =>
        SendAsync<BackupCommandResult>(HttpMethod.Post, "api/settings/backup", null, cancellationToken);
    public Task<SyncRunResult> RunSyncAsync(CancellationToken cancellationToken = default) =>
        SendAsync<SyncRunResult>(HttpMethod.Post, "api/settings/sync/run", null, cancellationToken);

    public Task<IReadOnlyList<SyncConflictItem>> SyncConflictsAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IReadOnlyList<SyncConflictItem>>(HttpMethod.Get, "api/settings/sync/conflicts", null, cancellationToken);

    public async Task RequeueConflictAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        // 204 NoContent doner; govde okumaya calisan genel SendAsync<T> burada kullanilamaz.
        using var request = Authorized(HttpMethod.Post, $"api/settings/sync/conflicts/{operationId:D}/requeue");
        using var response = await client.SendAsync(request, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }
    public Task<PagedResult<ApplicationLogItem>> LogsAsync(int page, int pageSize, CancellationToken cancellationToken = default) =>
        SendAsync<PagedResult<ApplicationLogItem>>(HttpMethod.Get, $"api/settings/logs?page={page}&pageSize={pageSize}", null, cancellationToken);
    public Task<SmsAutomationStatus> GetSmsAutomationAsync(CancellationToken cancellationToken = default) =>
        SendAsync<SmsAutomationStatus>(HttpMethod.Get, "api/settings/sms-automation", null, cancellationToken);
    public Task<SmsAutomationStatus> SaveSmsAutomationAsync(SmsAutomationSettings settings, CancellationToken cancellationToken = default) =>
        SendAsync<SmsAutomationStatus>(HttpMethod.Put, "api/settings/sms-automation", JsonContent.Create(settings), cancellationToken);
    public Task<EntitlementWarningRunResult> RunEntitlementWarningAsync(CancellationToken cancellationToken = default) =>
        SendAsync<EntitlementWarningRunResult>(HttpMethod.Post, "api/settings/sms-automation/run-entitlement-warning", null, cancellationToken);
    public Task<BackupValidationResult> ValidateBackupAsync(string path, CancellationToken cancellationToken = default) =>
        UploadAsync<BackupValidationResult>("api/settings/backup/validate", path, null, cancellationToken);
    public Task<RestoreResult> RestoreAsync(string path, string confirmation, CancellationToken cancellationToken = default) =>
        UploadAsync<RestoreResult>("api/settings/backup/restore", path, confirmation, cancellationToken);

    private async Task<T> SendAsync<T>(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = Authorized(method, url); request.Content = content;
        using var response = await client.SendAsync(request, cancellationToken); await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Ayarlar API yanıtı boş döndü.");
    }

    private async Task<T> UploadAsync<T>(string url, string path, string? confirmation, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var form = new MultipartFormDataContent();
        form.Add(new StreamContent(stream), "file", Path.GetFileName(path));
        if (confirmation is not null) form.Add(new StringContent(confirmation), "confirmation");
        return await SendAsync<T>(HttpMethod.Post, url, form, cancellationToken);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken); return request;
    }
    /// <summary>
    /// Sunucunun dogrulama mesajini ("Yedek saklama sayısı 1 ile 365 arasında olmalıdır." gibi)
    /// ApiRequestException ile ViewModel'e tasir. Once EnsureSuccessStatusCode her 4xx'i
    /// "Ayarlar servisine ulaşılamadı" + Cevrimdisi rozetine ceviriyordu; kullanici hangi
    /// alani duzeltecegini goremiyordu.
    /// </summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode) throw await ApiErrors.ReadAsync(response, cancellationToken);
    }
}
