using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECert.Controllers;

/// <summary>
/// The certificate-export control panel (إعدادات تصدير الشهادات), restricted to the built-in
/// SuperAdmin through the <c>manage-certificate-export-settings</c> permission. It edits the single
/// <see cref="CertificateExportSettings"/> row the PDF export endpoints read at runtime, so the
/// per-request ceiling and the public-download switch can be tuned per deployment — a small host
/// lowers the ceiling, a large one raises it — without touching the code.
/// </summary>
[Authorize]
public class CertificateExportSettingsController : Controller
{
    private readonly CertificateExportSettingsService _settings;
    private readonly AuditLogService _audit;

    public CertificateExportSettingsController(CertificateExportSettingsService settings, AuditLogService audit)
    {
        _settings = settings;
        _audit = audit;
    }

    private string Actor => User.Identity?.Name ?? "System";

    public async Task<IActionResult> Index()
    {
        if (!User.HasPermission(AdminPermissions.ManageCertificateExportSettings)) return Forbid();
        return View(await _settings.GetAsync());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(CertificateExportSettings model)
    {
        if (!User.HasPermission(AdminPermissions.ManageCertificateExportSettings)) return Forbid();

        if (model.MaxCertificatesPerExport < 1 || model.MaxCertificatesPerExport > 500)
        {
            TempData["Error"] = "الحد الأقصى للتصدير يجب أن يكون بين 1 و 500 شهادة.";
            return RedirectToAction(nameof(Index));
        }

        var settings = await _settings.GetAsync();
        settings.AllowPublicDownload = model.AllowPublicDownload;
        settings.MaxCertificatesPerExport = model.MaxCertificatesPerExport;
        settings.PageSize = model.PageSize;
        settings.IncludeBleedMarks = model.IncludeBleedMarks;
        settings.FileNamePrefix = string.IsNullOrWhiteSpace(model.FileNamePrefix) ? null : model.FileNamePrefix.Trim();

        await _settings.SaveAsync(settings, Actor);
        await _audit.LogAsync(Actor, "Update", "CertificateExportSettings", null, null, "Certificate export settings updated");
        TempData["Success"] = "تم حفظ إعدادات تصدير الشهادات.";
        return RedirectToAction(nameof(Index));
    }
}
