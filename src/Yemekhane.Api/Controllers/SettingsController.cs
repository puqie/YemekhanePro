using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Authorization;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Settings;
using Yemekhane.Api.Infrastructure;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Sync;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Route("api/settings")]
public sealed class SettingsController(ISettingsService settings, BackupService backups, YemekhaneDbContext db,
    IAuditService audit, SettingsSyncRunner syncRunner) : ControllerBase
{
    [HttpGet]
    [PermissionAuthorize(Permissions.SettingsRead)]
    public Task<SettingsDocument> Get(CancellationToken cancellationToken) => settings.GetAsync(cancellationToken);

    [HttpPut]
    [PermissionAuthorize(Permissions.SettingsManage)]
    public Task<SaveSettingsResult> Save(SaveSettingsRequest request, CancellationToken cancellationToken) =>
        settings.SaveAsync(request, cancellationToken);

    [HttpGet("logs")]
    [PermissionAuthorize(Permissions.SettingsRead)]
    public Task<Yemekhane.Application.Common.PagedResult<ApplicationLogItem>> Logs(
        [FromQuery] ApplicationLogQuery query, CancellationToken cancellationToken) => settings.LogsAsync(query, cancellationToken);

    [HttpGet("sync/status")]
    [PermissionAuthorize(Permissions.SettingsRead)]
    public Task<SyncStatus> SyncStatus(CancellationToken cancellationToken) => settings.SyncStatusAsync(cancellationToken);

    [HttpGet("sync/conflicts")]
    [PermissionAuthorize(Permissions.SettingsRead)]
    public Task<IReadOnlyList<SyncConflictItem>> SyncConflicts(CancellationToken cancellationToken) =>
        settings.SyncConflictsAsync(cancellationToken);

    /// <summary>Cakisan islemi yeniden kuyruga alir; karar operatorundur, motor kendiliginden cozmez.</summary>
    [HttpPost("sync/conflicts/{operationId:guid}/requeue")]
    [PermissionAuthorize(Permissions.SettingsManage)]
    public async Task<ActionResult> RequeueConflict(Guid operationId, CancellationToken cancellationToken)
    {
        await settings.SyncRequeueAsync(operationId, cancellationToken);
        audit.Record(new AuditEntry("SyncConflictRequeued", "SyncOperation", operationId.ToString(),
            "Çakışan senkronizasyon işlemi yeniden kuyruğa alındı."));
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [HttpPost("sync/run")]
    [PermissionAuthorize(Permissions.SettingsManage)]
    public async Task<ActionResult<SyncRunResult>> RunSync(CancellationToken cancellationToken)
    {
        var result = await syncRunner.RunAsync(cancellationToken);
        audit.Record(new AuditEntry("SyncRun", "SystemSetting", null, "Elle senkronizasyon çalıştırıldı.", result.Processed,
            After: new { result.Processed, result.Succeeded, result.RetryPending, result.PermanentFailures, result.Conflicts }));
        await db.SaveChangesAsync(cancellationToken);
        return Ok(result);
    }

    [HttpPost("backup")]
    [PermissionAuthorize(Permissions.BackupsManage)]
    public async Task<ActionResult<BackupCommandResult>> Backup(CancellationToken cancellationToken)
    {
        var result = await backups.CreateAsync(cancellationToken);
        await AuditAsync("BackupCreated", result.BackupId, result.FileName, cancellationToken);
        return Ok(new BackupCommandResult(result.BackupId, result.FileName, result.Manifest.CreatedAtUtc,
            result.Manifest.SchemaVersion, result.Manifest.AppVersion));
    }

    [HttpPost("backup/validate")]
    [PermissionAuthorize(Permissions.BackupsManage)]
    [RequestSizeLimit(2_147_483_648)]
    public async Task<ActionResult<BackupValidationResult>> ValidateBackup(IFormFile file, CancellationToken cancellationToken)
    {
        var path = await SaveUpload(file, cancellationToken);
        try
        {
            var result = await backups.ValidateAsync(path, cancellationToken);
            return Ok(new BackupValidationResult(result.BackupId, result.CreatedAtUtc, result.SchemaVersion, result.AppVersion, true));
        }
        finally { System.IO.File.Delete(path); }
    }

    [HttpPost("backup/restore")]
    [PermissionAuthorize(Permissions.BackupsManage)]
    [RequestSizeLimit(2_147_483_648)]
    public async Task<ActionResult<RestoreResult>> RestoreBackup(IFormFile file, [FromForm] string confirmation,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmation, "GERI YUKLE", StringComparison.Ordinal))
            return BadRequest(new ProblemDetails { Title = "Onay gerekli", Detail = "Geri yüklemek için tam olarak 'GERI YUKLE' yazın." });
        var path = await SaveUpload(file, cancellationToken);
        try
        {
            var result = await backups.RestoreAsync(path, cancellationToken);
            await AuditAsync("BackupRestored", result.BackupId, result.SafetyBackupFileName, cancellationToken);
            return Ok(result);
        }
        finally { System.IO.File.Delete(path); }
    }

    private static async Task<string> SaveUpload(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > 2_147_483_648) throw new ArgumentException("Backup arşivi boyutu geçersiz.");
        var directory = Path.Combine(Path.GetTempPath(), "YemekhanePro"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings-upload-" + Guid.NewGuid().ToString("N") + ".zip");
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await file.CopyToAsync(stream, cancellationToken); return path;
    }

    private async Task AuditAsync(string action, Guid id, string metadata, CancellationToken cancellationToken)
    {
        audit.Record(new AuditEntry(action, "Backup", id.ToString(), action == "BackupCreated" ? "Yedek oluşturuldu." : "Yedek geri yüklendi.", After: new { Metadata = metadata }));
        await db.SaveChangesAsync(cancellationToken);
    }
}
