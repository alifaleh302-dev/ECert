using ECert.Data;
using ECert.Services;
using ECert.Services.Excel;
using ECert.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Two-phase import of certificates issued by the old Excel-based system. Phase one parses and
/// validates the file into a preview the administrator can edit — including per-course category,
/// instructors and a create-or-link decision. Phase two commits the approved rows, generating the
/// modern artefacts (public id, verification code, QR link) for each one.
/// </summary>
[Authorize]
public class CertificatesImportController : Controller
{
    private const string PreviewCategory = "ecert-legacy-certificate-imports";
    private const string ResultSessionKey = "LegacyCertificateImportResult";

    private readonly ECertDbContext _db;
    private readonly LegacyCertificateImportService _importService;
    private readonly CertificateQrService _qr;
    private readonly IConfiguration _configuration;

    public CertificatesImportController(
        ECertDbContext db,
        LegacyCertificateImportService importService,
        CertificateQrService qr,
        IConfiguration configuration)
    {
        _db = db;
        _importService = importService;
        _qr = qr;
        _configuration = configuration;
    }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    [HttpGet]
    public async Task<IActionResult> Import()
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        var model = new LegacyCertificateImportPageViewModel();
        await PopulateOptionsAsync(model);
        return View(model);
    }

    [HttpGet]
    public IActionResult DownloadTemplate()
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        using var export = new ExcelExport("الشهادات القديمة",
            "الاسم بالعربية", "الاسم بالإنجليزية", "رقم الشهادة", "اسم الدورة", "تاريخ الإصدار");
        export.WriteSampleRow("محمد أحمد", "Mohammed Ahmed", "CERT-2020-0001", "دورة الإسعافات الأولية", "2020-06-15");
        return export.Build("legacy-certificates-template");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(10_000_000)]
    public async Task<IActionResult> Import(LegacyCertificateImportPageViewModel model)
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        await PopulateOptionsAsync(model);

        if (ExcelImport.ValidateUpload(model.File) is { } uploadError)
            ModelState.AddModelError(nameof(model.File), uploadError);

        if (!ModelState.IsValid)
            return View(model);

        try
        {
            await using var stream = model.File!.OpenReadStream();
            var preview = await _importService.BuildPreviewAsync(stream, model.DefaultInstructorId, model.DefaultCategoryId);

            preview.CreatedAtUtc = DateTime.UtcNow;
            preview.Token = ExcelPreviewStore.Save(PreviewCategory, preview);
            model.Preview = preview;
            return View(model);
        }
        catch (ExcelImportException ex)
        {
            ModelState.AddModelError(nameof(model.File), ex.Message);
            return View(model);
        }
    }

    /// <summary>Edits a single preview row in place and re-validates it. Returns the refreshed state as JSON.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditRow(string token, int rowNumber, string? fullNameArabic, string? fullNameEnglish, string? certificateNumber, string? courseKey, string? issueDate)
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        var preview = ExcelPreviewStore.Peek<LegacyCertificateImportPreview>(PreviewCategory, token);
        if (preview is null)
            return BadRequest(new { message = "انتهت صلاحية المعاينة. ارفع الملف مرة أخرى." });

        var row = preview.Rows.FirstOrDefault(r => r.RowNumber == rowNumber);
        if (row is null)
            return NotFound(new { message = "الصف غير موجود." });

        row.FullNameArabic = (fullNameArabic ?? string.Empty).Trim();
        row.FullNameEnglish = (fullNameEnglish ?? string.Empty).Trim();
        row.CertificateNumber = CollapseSpaces(certificateNumber);
        row.IssueDate = ParseDate(issueDate);

        var normalizedKey = CollapseSpaces(courseKey).ToLowerInvariant();
        var group = preview.Courses.FirstOrDefault(c => string.Equals(c.Key, normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (group is not null)
        {
            row.CourseKey = group.Key;
            row.CourseName = group.NameArabic;
        }

        await RevalidateRowAsync(preview, row);
        Recalculate(preview);
        ExcelPreviewStore.Replace(PreviewCategory, token, preview);

        return Json(new
        {
            row.RowNumber,
            row.IsValid,
            row.IsDuplicate,
            errors = row.Errors,
            validRows = preview.ValidRows,
            duplicateRows = preview.DuplicateRows,
            invalidRows = preview.InvalidRows,
            canCommit = preview.CanCommit
        });
    }

    /// <summary>Saves the category, instructors and create/link choice of one course group.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> EditCourse(
        string token,
        string courseKey,
        int? categoryId,
        int? linkedCourseId,
        string? action,
        string? nameEnglish,
        List<int>? instructorIds)
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        var preview = ExcelPreviewStore.Peek<LegacyCertificateImportPreview>(PreviewCategory, token);
        if (preview is null)
            return BadRequest(new { message = "انتهت صلاحية المعاينة. ارفع الملف مرة أخرى." });

        var group = preview.Courses.FirstOrDefault(c => string.Equals(c.Key, courseKey, StringComparison.OrdinalIgnoreCase));
        if (group is null)
            return NotFound(new { message = "الدورة غير موجودة في المعاينة." });

        var chosenAction = string.Equals(action, "link", StringComparison.OrdinalIgnoreCase)
            ? LegacyCourseAction.Link
            : LegacyCourseAction.Create;

        group.Action = chosenAction;
        group.NameEnglish = (nameEnglish ?? string.Empty).Trim();
        group.InstructorIds = (instructorIds ?? new List<int>()).Distinct().ToList();
        group.InstructorNames = await ResolveInstructorNamesAsync(group.InstructorIds);

        var errors = new List<string>();

        if (categoryId is int categoryIdValue && await _db.Categories.AnyAsync(c => c.CategoryId == categoryIdValue))
        {
            group.CategoryId = categoryIdValue;
        }
        else
        {
            group.CategoryId = null;
            errors.Add("اختر تصنيف الدورة.");
        }

        if (group.InstructorIds.Count == 0)
            errors.Add("اختر مدرباً واحداً على الأقل.");

        if (chosenAction == LegacyCourseAction.Link)
        {
            var linked = linkedCourseId is int linkId
                ? await _db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.CourseId == linkId)
                : null;
            if (linked is null)
            {
                group.LinkedCourseId = null;
                group.LinkedCourseLabel = string.Empty;
                errors.Add("اختر الدورة الموجودة التي سيتم الربط بها.");
            }
            else
            {
                group.LinkedCourseId = linked.CourseId;
                group.LinkedCourseLabel = linked.CourseNameArabic ?? linked.CourseName;
            }
        }
        else
        {
            group.LinkedCourseId = null;
            group.LinkedCourseLabel = string.Empty;
        }

        Recalculate(preview);
        ExcelPreviewStore.Replace(PreviewCategory, token, preview);

        return Json(new
        {
            courseKey = group.Key,
            isConfigured = group.IsConfigured,
            errors,
            newCourseCount = preview.NewCourseCount,
            linkedCourseCount = preview.LinkedCourseCount,
            canCommit = preview.CanCommit
        });
    }

    /// <summary>Applies one category and/or one set of instructors to every course group at once.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ApplyCourseDefaults(string token, int? categoryId, List<int>? instructorIds)
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        var preview = ExcelPreviewStore.Peek<LegacyCertificateImportPreview>(PreviewCategory, token);
        if (preview is null)
            return BadRequest(new { message = "انتهت صلاحية المعاينة. ارفع الملف مرة أخرى." });

        var names = Array.Empty<string>();
        if (instructorIds is { Count: > 0 })
        {
            var distinct = instructorIds.Distinct().ToList();
            names = (await _db.Instructors.AsNoTracking()
                .Where(i => distinct.Contains(i.InstructorId))
                .Select(i => i.FullName)
                .ToListAsync()).ToArray();
        }

        var categoryValid = categoryId is int categoryIdValue && await _db.Categories.AnyAsync(c => c.CategoryId == categoryIdValue);

        foreach (var group in preview.Courses)
        {
            if (categoryValid)
                group.CategoryId = categoryId;

            if (instructorIds is { Count: > 0 })
            {
                group.InstructorIds = instructorIds.Distinct().ToList();
                group.InstructorNames = names.ToList();
            }
        }

        Recalculate(preview);
        ExcelPreviewStore.Replace(PreviewCategory, token, preview);

        return Json(new
        {
            canCommit = preview.CanCommit,
            newCourseCount = preview.NewCourseCount,
            linkedCourseCount = preview.LinkedCourseCount
        });
    }

    /// <summary>Searches the courses already in the system, for the "link to existing" picker.</summary>
    [HttpGet]
    public async Task<IActionResult> SearchCourses(string? term)
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        var results = await _importService.SearchCoursesAsync(term);
        return Json(results.Select(c => new { id = c.Id, name = c.Name }));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmImport(string token)
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        var preview = ExcelPreviewStore.Take<LegacyCertificateImportPreview>(PreviewCategory, token);
        if (preview is null)
        {
            TempData["Error"] = "انتهت صلاحية المعاينة. ارفع الملف مرة أخرى.";
            return RedirectToAction(nameof(Import));
        }

        if (ExcelPreviewStore.IsExpired(preview.CreatedAtUtc))
        {
            TempData["Error"] = "انتهت صلاحية المعاينة. ارفع الملف مرة أخرى.";
            return RedirectToAction(nameof(Import));
        }

        if (!preview.CanCommit)
        {
            TempData["Error"] = "لا يوجد أي صف صالح للاستيراد، أو لم تكتمل إعدادات إحدى الدورات.";
            return RedirectToAction(nameof(Import));
        }

        var actor = User.Identity?.Name ?? "System";
        var result = await _importService.CommitAsync(preview, actor, GetPublicBaseUrl(), HttpContext.Connection.RemoteIpAddress?.ToString());
        TempData[ResultSessionKey] = System.Text.Json.JsonSerializer.Serialize(result);

        return RedirectToAction(nameof(Result));
    }

    [HttpGet]
    public IActionResult Result()
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        if (TempData[ResultSessionKey] is not string json)
            return RedirectToAction(nameof(Import));

        var result = System.Text.Json.JsonSerializer.Deserialize<LegacyCertificateImportResult>(json);
        return result is null ? RedirectToAction(nameof(Import)) : View(result);
    }

    private async Task PopulateOptionsAsync(LegacyCertificateImportPageViewModel model)
    {
        model.Instructors = await _db.Instructors.AsNoTracking()
            .OrderBy(i => i.FullName)
            .Select(i => new LegacyImportOption { Id = i.InstructorId, Name = i.FullName })
            .ToListAsync();
        model.Categories = await _db.Categories.AsNoTracking()
            .OrderBy(c => c.CategoryName)
            .Select(c => new LegacyImportOption { Id = c.CategoryId, Name = c.CategoryName })
            .ToListAsync();
    }

    private async Task<List<string>> ResolveInstructorNamesAsync(List<int> instructorIds)
    {
        if (instructorIds.Count == 0) return new List<string>();

        return await _db.Instructors.AsNoTracking()
            .Where(i => instructorIds.Contains(i.InstructorId))
            .Select(i => i.FullName)
            .ToListAsync();
    }

    private async Task RevalidateRowAsync(LegacyCertificateImportPreview preview, LegacyCertificateImportRow row)
    {
        var errors = new List<string>();
        if (row.FullNameArabic.Length == 0) errors.Add("الاسم بالعربية مطلوب.");
        if (row.CertificateNumber.Length == 0) errors.Add("رقم الشهادة مطلوب.");
        if (row.CourseKey.Length == 0) errors.Add("اسم الدورة مطلوب.");

        row.IsDuplicate = false;

        if (errors.Count == 0)
        {
            var duplicateInFile = preview.Rows
                .Any(r => !ReferenceEquals(r, row) && string.Equals(r.CertificateNumber, row.CertificateNumber, StringComparison.OrdinalIgnoreCase));
            if (duplicateInFile)
            {
                row.IsDuplicate = true;
                errors.Add("رقم الشهادة مكرر داخل الملف — سيتم تجاهله.");
            }
            else if (await _db.Certificates.AsNoTracking().AnyAsync(c => c.CertificateNumber == row.CertificateNumber))
            {
                row.IsDuplicate = true;
                errors.Add("رقم الشهادة موجود مسبقاً في النظام — سيتم تجاهله.");
            }
        }

        row.Errors = errors;
        row.IsValid = errors.Count == 0;
    }

    private static void Recalculate(LegacyCertificateImportPreview preview)
    {
        preview.ValidRows = preview.Rows.Count(r => r.IsValid);
        preview.DuplicateRows = preview.Rows.Count(r => r.IsDuplicate);
        preview.InvalidRows = preview.Rows.Count(r => !r.IsValid && !r.IsDuplicate);

        foreach (var group in preview.Courses)
            group.HasValidRows = preview.Rows.Any(r => r.IsValid && string.Equals(r.CourseKey, group.Key, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            || DateTime.TryParse(value, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out date)
            ? date.Date
            : null;
    }

    private static string CollapseSpaces(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private string GetPublicBaseUrl()
    {
        var configured = _configuration["PUBLIC_BASE_URL"] ?? _configuration["CertificateSecurity:PublicBaseUrl"];
        return !string.IsNullOrWhiteSpace(configured) ? configured.Trim() : $"{Request.Scheme}://{Request.Host}";
    }
}
