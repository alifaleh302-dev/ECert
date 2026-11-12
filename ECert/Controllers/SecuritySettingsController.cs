using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECert.Controllers;

/// <summary>
/// The security control panel (إعدادات الأمان والنسخ الاحتياطي), restricted to SuperAdmin through
/// the <c>manage-security-settings</c> permission. It edits the single <see cref="SecuritySettings"/>
/// row the security services read at runtime, so policy — lockout thresholds, Telegram channels,
/// the backup key and 2FA — can be changed here without touching the code.
/// </summary>
[Authorize]
public class SecuritySettingsController : Controller
{
    private readonly SecuritySettingsService _settings;
    private readonly AuditLogService _audit;
    private readonly AuditIntegrityService _integrity;
    private readonly DatabaseBackupService _backup;
    private readonly TelegramLinkService _link;

    public SecuritySettingsController(
        SecuritySettingsService settings,
        AuditLogService audit,
        AuditIntegrityService integrity,
        DatabaseBackupService backup,
        TelegramLinkService link)
    {
        _settings = settings;
        _audit = audit;
        _integrity = integrity;
        _backup = backup;
        _link = link;
    }

    private string Actor => User.Identity?.Name ?? "System";
    private bool IsSuperAdmin => User.IsSuperAdmin();

    public async Task<IActionResult> Index()
    {
        if (!IsSuperAdmin) return Forbid();
        return View(await _settings.GetAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SecuritySettings model)
    {
        if (!IsSuperAdmin) return Forbid();

        if (model.MaxFailedLoginAttempts < 1 || model.LockoutMinutes < 1 || model.IdleTimeoutMinutes < 1)
        {
            TempData["Error"] = "قيم الحظر والمهلة يجب أن تكون أكبر من صفر.";
            return RedirectToAction(nameof(Index));
        }

        var settings = await _settings.GetAsync();
        settings.TelegramBotToken = model.TelegramBotToken?.Trim();
        settings.SecurityAlertChannelId = model.SecurityAlertChannelId?.Trim();
        settings.BackupChannelId = model.BackupChannelId?.Trim();
        settings.TwoFactorEnabled = model.TwoFactorEnabled;
        settings.MaxFailedLoginAttempts = model.MaxFailedLoginAttempts;
        settings.LockoutMinutes = model.LockoutMinutes;
        settings.IdleTimeoutMinutes = model.IdleTimeoutMinutes;
        settings.BackupEnabled = model.BackupEnabled;
        settings.BackupHourUtc = Math.Clamp(model.BackupHourUtc, 0, 23);

        // The encryption key is write-only in the UI: an empty box means "keep the current key",
        // so the secret is never rendered back into the page.
        if (!string.IsNullOrWhiteSpace(model.BackupEncryptionKey))
            settings.BackupEncryptionKey = model.BackupEncryptionKey.Trim();

        await _settings.SaveAsync(settings, Actor);
        await _audit.LogAsync(Actor, "Update", "SecuritySettings", null, null, "Security settings updated");
        TempData["Success"] = "تم حفظ الإعدادات الأمنية.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunBackupNow()
    {
        if (!IsSuperAdmin) return Forbid();
        var result = await _backup.RunAsync();
        await _audit.LogAsync(Actor, "Backup", "Database", null, null, result);
        TempData[result.Contains("تم") ? "Success" : "Error"] = result;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PollTelegramLinks()
    {
        if (!IsSuperAdmin) return Forbid();
        var linked = await _link.PollOnceAsync(0);
        TempData["Success"] = linked > 0 ? $"تم ربط {linked} حساب." : "لا توجد طلبات ربط جديدة.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Recomputes the audit hash chain and reports the first row that fails.</summary>
    [HttpGet]
    public async Task<IActionResult> VerifyIntegrity()
    {
        if (!IsSuperAdmin) return Forbid();
        var operations = await _integrity.VerifyOperationLogAsync();
        var financial = await _integrity.VerifyFinancialAuditAsync();
        return View((operations, financial));
    }
}
