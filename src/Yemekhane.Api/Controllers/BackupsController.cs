using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Yemekhane.Api.Infrastructure;
using Yemekhane.Infrastructure.Backup;
using Yemekhane.Infrastructure.Persistence;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Notifications;

namespace Yemekhane.Api.Controllers;

[ApiController]
[Authorize(Policy = BackupAuthorizationPolicies.ManageBackups)]
[Route("api/backups")]
public sealed class BackupsController(BackupService backupService, YemekhaneDbContext dbContext, IAuditService auditService,
    NotificationService notifications) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<object>> Create(CancellationToken cancellationToken)
    {
        var result = await backupService.CreateAsync(cancellationToken);
        await AuditAsync("BackupCreated", result.BackupId, result.FileName, cancellationToken);
        await notifications.CreateAsync(new CreateNotification(NotificationSeverities.Success, "BackupCreated",
            "Yedekleme tamamlandı", result.FileName, "Backup", result.BackupId.ToString("D"), "settings", AudiencePermission: "backups.manage",
            DeduplicationKey: $"backup:{result.BackupId:D}"), cancellationToken);
        return Ok(new
        {
            result.BackupId,
            result.FileName,
            result.Manifest.CreatedAtUtc,
            result.Manifest.CreatedAtIstanbul,
            result.Manifest.SchemaVersion,
            result.Manifest.AppVersion
        });
    }

    [HttpPost("restore")]
    [RequestSizeLimit(2_147_483_648)]
    public async Task<ActionResult<RestoreResult>> Restore(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > 2_147_483_648)
            return BadRequest("Backup arşivi boyutu geçersiz.");

        var temporaryPath = Path.Combine(Path.GetTempPath(), "YemekhanePro", "upload-" + Guid.NewGuid().ToString("N") + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(temporaryPath)!);
        try
        {
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await file.CopyToAsync(target, cancellationToken);
            var result = await backupService.RestoreAsync(temporaryPath, cancellationToken);
            await AuditAsync("BackupRestored", result.BackupId, result.SafetyBackupFileName, cancellationToken);
            await notifications.CreateAsync(new CreateNotification(NotificationSeverities.Success, "BackupRestored",
                "Geri yükleme tamamlandı", "Veritabanı yedekten geri yüklendi.", "Backup", result.BackupId.ToString("D"), "settings",
                AudiencePermission: "backups.manage", DeduplicationKey: $"restore:{result.BackupId:D}"), cancellationToken);
            return Ok(result);
        }
        finally
        {
            if (System.IO.File.Exists(temporaryPath)) System.IO.File.Delete(temporaryPath);
        }
    }

    private async Task AuditAsync(string action, Guid backupId, string description, CancellationToken cancellationToken)
    {
        auditService.Record(new AuditEntry(action, "Backup", backupId.ToString(),
            action == "BackupCreated" ? "Yedek oluşturuldu." : "Yedek geri yüklendi.",
            After: new { Metadata = description }));
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
