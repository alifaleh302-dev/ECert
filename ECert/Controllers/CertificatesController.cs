using System.Net.Http.Json;
using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services;
using ECert.Services.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class CertificatesController : Controller
{
    private const string InvalidVerifyMessage = "رقم الشهادة غير صحيح أو غير موجود.";
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly NotificationService _notify;
    private readonly CertificateSecurityService _certificateSecurity;
    private readonly VerifyRequestGuardService _verifyGuard;
    private readonly IConfiguration _configuration;
    private readonly TurnstileService _turnstile;
    private readonly CertificateRenderService _renderService;
    private readonly CertificatePdfService _pdfService;
    private readonly CertificateExportSettingsService _exportSettings;
    private readonly CertificateQrService _qrService;

    public CertificatesController(
        ECertDbContext db,
        AuditLogService audit,
        NotificationService notify,
        CertificateSecurityService certificateSecurity,
        VerifyRequestGuardService verifyGuard,
        IConfiguration configuration,
        TurnstileService turnstile,
        CertificateRenderService renderService,
        CertificatePdfService pdfService,
        CertificateExportSettingsService exportSettings,
        CertificateQrService qrService)
    {
        _db = db;
        _audit = audit;
        _notify = notify;
        _certificateSecurity = certificateSecurity;
        _verifyGuard = verifyGuard;
        _configuration = configuration;
        _turnstile = turnstile;
        _renderService = renderService;
        _pdfService = pdfService;
        _exportSettings = exportSettings;
        _qrService = qrService;
    }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    [HttpGet]
    public async Task<IActionResult> Index(string? search, int? courseId, DateTime? issueFrom, DateTime? issueTo)
    {
        if (!User.HasAnyPermission(AdminPermissions.IssueCertificates, AdminPermissions.ViewCertificates)) return Forbid();

        var query = _db.Certificates
            .InScope(User)
            .Include(c => c.Registration)
            .ThenInclude(r => r!.Course)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(c =>
                c.TraineeName.Contains(trimmedSearch) ||
                (c.CourseNameEnglish != null && c.CourseNameEnglish.Contains(trimmedSearch)) ||
                (c.CourseNameArabic != null && c.CourseNameArabic.Contains(trimmedSearch)) ||
                c.CourseName.Contains(trimmedSearch) ||
                c.CertificateNumber.Contains(trimmedSearch) ||
                c.PublicId.Contains(trimmedSearch));
        }

        if (courseId.HasValue)
            query = query.Where(c => c.Registration != null && c.Registration.CourseId == courseId.Value);

        if (issueFrom.HasValue)
        {
            var start = issueFrom.Value.Date;
            query = query.Where(c => c.IssueDate >= start);
        }

        if (issueTo.HasValue)
        {
            var end = issueTo.Value.Date.AddDays(1);
            query = query.Where(c => c.IssueDate < end);
        }

        var certificates = await query
            .OrderByDescending(c => c.IssueDate)
            .ThenByDescending(c => c.CertificateId)
            .ToListAsync();

        var model = new CertificateIndexPageViewModel
        {
            Search = search,
            CourseId = courseId,
            IssueFrom = issueFrom,
            IssueTo = issueTo,
            Courses = await _db.Courses.InScope(User).OrderBy(c => c.CourseNameArabic).ToListAsync(),
            Certificates = certificates,
            CanManage = HasPermission(AdminPermissions.IssueCertificates),
            VerificationUrls = certificates.ToDictionary(
                c => c.CertificateId,
                c => BuildVerificationUrl(c))
        };

        return View(model);
    }

    [HttpGet]
    public async Task<IActionResult> Issue(int? courseId, DateTime? registrationFrom, DateTime? registrationTo)
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        var query = _db.Registrations
            .InScope(User)
            .Include(r => r.Course)
            .Where(r => r.Status == "Accepted" && !r.CertificateIssued && r.Course != null && r.Course.Status == "Completed")
            .AsQueryable();

        if (courseId.HasValue)
            query = query.Where(r => r.CourseId == courseId.Value);

        if (registrationFrom.HasValue)
        {
            var start = registrationFrom.Value.Date;
            query = query.Where(r => r.RegistrationDate >= start);
        }

        if (registrationTo.HasValue)
        {
            var end = registrationTo.Value.Date.AddDays(1);
            query = query.Where(r => r.RegistrationDate < end);
        }

        var model = new CertificateIssuePageViewModel
        {
            CourseId = courseId,
            RegistrationFrom = registrationFrom,
            RegistrationTo = registrationTo,
            Courses = await _db.Courses.InScope(User).Where(c => c.Status == "Completed").OrderBy(c => c.CourseNameArabic).ToListAsync(),
            EligibleRegistrations = await query.OrderByDescending(r => r.RegistrationDate).ThenBy(r => r.FullName).ToListAsync()
        };

        return View(model);
    }

    [HttpGet]
    public IActionResult IssueBulk()
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        TempData["Error"] = "افتح صفحة إصدار الشهادات وحدد المتدربين قبل تنفيذ الإصدار.";
        return RedirectToAction(nameof(Issue));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> IssueBulk(int[] registrationIds, string? certificatePrefix)
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        var ids = registrationIds?.Where(id => id > 0).Distinct().ToArray() ?? Array.Empty<int>();
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            TempData["Error"] = "الرجاء تحديد متدرب واحد على الأقل لإصدار الشهادات.";
            return RedirectToAction(nameof(Issue));
        }

        string normalizedCertificatePrefix;
        try
        {
            normalizedCertificatePrefix = _certificateSecurity.NormalizeCertificatePrefix(certificatePrefix);
        }
        catch (ArgumentException exception)
        {
            TempData["Error"] = exception.Message;
            return RedirectToAction(nameof(Issue));
        }

        var registrations = await _db.Registrations
            .InScope(User)
            .Include(r => r.Course)
            .Where(r => idList.Contains(r.RegistrationId) && r.Status == "Accepted" && !r.CertificateIssued && r.Course != null && r.Course.Status == "Completed")
            .OrderBy(r => r.RegistrationId)
            .ToListAsync();

        if (!registrations.Any())
        {
            TempData["Error"] = "العناصر المحددة غير مؤهلة لإصدار الشهادات أو تم إصدارها مسبقاً.";
            return RedirectToAction(nameof(Issue));
        }

        var issuedCertificates = new List<Certificate>();
        var reservedCertificateNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedPublicIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedVerificationCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var transaction = await _db.Database.BeginTransactionAsync();

        foreach (var registration in registrations)
        {
            var certificate = new Certificate
            {
                CertificateNumber = await GenerateUniqueCertificateNumberAsync(normalizedCertificatePrefix, reservedCertificateNumbers),
                PublicId = await GenerateUniquePublicIdAsync(reservedPublicIds),
                VerificationCode = await GenerateUniqueVerificationCodeAsync(reservedVerificationCodes),
                RegistrationId = registration.RegistrationId,
                TraineeName = registration.FullNameArabic ?? registration.FullName,
                TraineeNameArabic = registration.FullNameArabic ?? registration.FullName,
                TraineeNameEnglish = registration.FullNameEnglish ?? registration.FullName,
                CourseName = registration.Course?.CourseNameArabic ?? registration.Course?.CourseName ?? string.Empty,
                CourseNameEnglish = registration.Course?.CourseNameEnglish ?? registration.Course?.CourseName ?? string.Empty,
                CourseNameArabic = registration.Course?.CourseNameArabic ?? registration.Course?.CourseName ?? string.Empty,
                IssueDate = DateTime.Now,
                IssuedBy = User.Identity?.Name ?? "System",
                Status = "Valid",
                SignatureVersion = 1,
                CreatedAt = DateTime.Now
            };

            issuedCertificates.Add(certificate);
            _db.Certificates.Add(certificate);
            registration.CertificateIssued = true;
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        var issuedBy = User.Identity?.Name ?? "System";
        var certNumbers = string.Join(", ", issuedCertificates.Select(c => c.CertificateNumber));
        await _audit.LogAsync(issuedBy, "IssueBulk", "Certificate", null, null, $"Prefix: {normalizedCertificatePrefix}; Count: {issuedCertificates.Count}; Certificates: {certNumbers}", HttpContext.Connection.RemoteIpAddress?.ToString());

        foreach (var registration in registrations)
        {
            var certificate = issuedCertificates.First(c => c.RegistrationId == registration.RegistrationId);
            await _notify.SendAsync(
                registration.FullName,
                registration.Phone,
                registration.Email,
                "CertificateIssued",
                $"عزيزي {registration.FullName}، تم إصدار شهادتك في دورة {registration.Course?.CourseNameEnglish} / {registration.Course?.CourseNameArabic}. رقم الشهادة: {certificate.CertificateNumber}",
                "SMS",
                "Certificate",
                certificate.CertificateId);
        }

        TempData["Success"] = $"تم إصدار {issuedCertificates.Count} شهادة بنجاح بالبادئة {normalizedCertificatePrefix}.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportSelected(int[] certificateIds)
    {
        if (!User.HasAnyPermission(AdminPermissions.IssueCertificates, AdminPermissions.ViewCertificates)) return Forbid();

        var ids = certificateIds?.Where(id => id > 0).Distinct().ToArray() ?? Array.Empty<int>();
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            TempData["Error"] = "الرجاء تحديد شهادة واحدة على الأقل للتصدير.";
            return RedirectToAction(nameof(Index));
        }

        var certificates = await _db.Certificates
            .InScope(User)
            .Include(c => c.Registration)
            .ThenInclude(r => r!.Course)
            .Where(c => idList.Contains(c.CertificateId))
            .OrderByDescending(c => c.IssueDate)
            .ToListAsync();

        if (!certificates.Any())
        {
            TempData["Error"] = "لم يتم العثور على شهادات مطابقة للتصدير.";
            return RedirectToAction(nameof(Index));
        }

        return BuildExcelExport(certificates, "selected");
    }

    [HttpGet]
    public async Task<IActionResult> ExportFiltered(string? search, int? courseId, DateTime? issueFrom, DateTime? issueTo)
    {
        if (!User.HasAnyPermission(AdminPermissions.IssueCertificates, AdminPermissions.ViewCertificates)) return Forbid();

        var query = _db.Certificates
            .InScope(User)
            .Include(c => c.Registration)
            .ThenInclude(r => r!.Course)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(c =>
                c.TraineeName.Contains(trimmedSearch) ||
                (c.CourseNameEnglish != null && c.CourseNameEnglish.Contains(trimmedSearch)) ||
                (c.CourseNameArabic != null && c.CourseNameArabic.Contains(trimmedSearch)) ||
                c.CourseName.Contains(trimmedSearch) ||
                c.CertificateNumber.Contains(trimmedSearch) ||
                c.PublicId.Contains(trimmedSearch));
        }

        if (courseId.HasValue)
            query = query.Where(c => c.Registration != null && c.Registration.CourseId == courseId.Value);

        if (issueFrom.HasValue)
        {
            var start = issueFrom.Value.Date;
            query = query.Where(c => c.IssueDate >= start);
        }

        if (issueTo.HasValue)
        {
            var end = issueTo.Value.Date.AddDays(1);
            query = query.Where(c => c.IssueDate < end);
        }

        var certificates = await query
            .OrderByDescending(c => c.IssueDate)
            .ThenByDescending(c => c.CertificateId)
            .ToListAsync();

        if (!certificates.Any())
        {
            TempData["Error"] = "لا توجد شهادات مطابقة للفلاتر الحالية للتصدير.";
            return RedirectToAction(nameof(Index), new { search, courseId, issueFrom, issueTo });
        }

        return BuildExcelExport(certificates, "filtered");
    }

    /// <summary>
    /// Renders the selected certificates as one print-ready PDF (one certificate per page) using
    /// the linked template design. The batch ceiling comes from
    /// <see cref="CertificateExportSettings"/> so a SuperAdmin can size it to the host's resources.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ExportSelectedPdf(int[] certificateIds)
    {
        if (!User.HasAnyPermission(AdminPermissions.IssueCertificates, AdminPermissions.ViewCertificates)) return Forbid();

        var ids = certificateIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
        if (ids.Count == 0)
        {
            TempData["Error"] = "الرجاء تحديد شهادة واحدة على الأقل للتصدير.";
            return RedirectToAction(nameof(Index));
        }

        var settings = await _exportSettings.GetAsync();
        if (ids.Count > settings.MaxCertificatesPerExport)
        {
            TempData["Error"] = $"لا يمكن تصدير أكثر من {settings.MaxCertificatesPerExport} شهادة في الملف الواحد. الرجاء تقليل التحديد.";
            return RedirectToAction(nameof(Index));
        }

        var certificates = await LoadForExportAsync(ids);
        if (certificates.Count == 0)
        {
            TempData["Error"] = "لم يتم العثور على شهادات مطابقة للتصدير.";
            return RedirectToAction(nameof(Index));
        }

        return await BuildPdfExportAsync(certificates, settings, "selected");
    }

    /// <summary>
    /// Renders every certificate matching the current list filters as one PDF, bounded by the
    /// configured batch ceiling.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ExportFilteredPdf(string? search, int? courseId, DateTime? issueFrom, DateTime? issueTo)
    {
        if (!User.HasAnyPermission(AdminPermissions.IssueCertificates, AdminPermissions.ViewCertificates)) return Forbid();

        var settings = await _exportSettings.GetAsync();
        var query = ApplyCertificateFilters(
            _db.Certificates.InScope(User).AsQueryable(), search, courseId, issueFrom, issueTo);

        var certificates = await query
            .OrderByDescending(c => c.IssueDate)
            .ThenByDescending(c => c.CertificateId)
            .Take(settings.MaxCertificatesPerExport)
            .ToListAsync();

        if (certificates.Count == 0)
        {
            TempData["Error"] = "لا توجد شهادات مطابقة للفلاتر الحالية للتصدير.";
            return RedirectToAction(nameof(Index), new { search, courseId, issueFrom, issueTo });
        }

        return await BuildPdfExportAsync(certificates, settings, "filtered");
    }

    /// <summary>
    /// Offers the certificate PDF on the public verification page, but only when a SuperAdmin has
    /// allowed it. The signature check is the same one the page already passed, so only the holder
    /// of the signed link can download.
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    [EnableRateLimiting("public-verification")]
    public async Task<IActionResult> PublicPdf(string publicId, string? sig)
    {
        if (!IsVerifyRequestAllowed())
            return StatusCode(StatusCodes.Status429TooManyRequests);

        if (string.IsNullOrWhiteSpace(publicId) || !_certificateSecurity.VerifySignature(publicId, sig))
            return NotFound();

        var settings = await _exportSettings.GetAsync();
        if (!settings.AllowPublicDownload) return NotFound();

        var normalizedPublicId = publicId.Trim();
        var certificate = await _db.Certificates
            .AsNoTracking()
            .Include(c => c.Registration)
            .ThenInclude(r => r!.Course)
            .ThenInclude(course => course!.Partners)
            .FirstOrDefaultAsync(c => c.PublicId == normalizedPublicId);

        if (certificate == null) return NotFound();

        return await BuildPdfExportAsync(new List<Certificate> { certificate }, settings, "public", download: true);
    }

    private async Task<List<Certificate>> LoadForExportAsync(List<int> ids)
        => await _db.Certificates
            .InScope(User)
            .Include(c => c.Registration)
            .ThenInclude(r => r!.Course)
            .ThenInclude(course => course!.Partners)
            .Where(c => ids.Contains(c.CertificateId))
            .OrderByDescending(c => c.IssueDate)
            .ThenByDescending(c => c.CertificateId)
            .ToListAsync();

    private static IQueryable<Certificate> ApplyCertificateFilters(
        IQueryable<Certificate> query, string? search, int? courseId, DateTime? issueFrom, DateTime? issueTo)
    {
        query = query.Include(c => c.Registration).ThenInclude(r => r!.Course).ThenInclude(course => course!.Partners);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmedSearch = search.Trim();
            query = query.Where(c =>
                c.TraineeName.Contains(trimmedSearch) ||
                (c.CourseNameEnglish != null && c.CourseNameEnglish.Contains(trimmedSearch)) ||
                (c.CourseNameArabic != null && c.CourseNameArabic.Contains(trimmedSearch)) ||
                c.CourseName.Contains(trimmedSearch) ||
                c.CertificateNumber.Contains(trimmedSearch) ||
                c.PublicId.Contains(trimmedSearch));
        }

        if (courseId.HasValue)
            query = query.Where(c => c.Registration != null && c.Registration.CourseId == courseId.Value);

        if (issueFrom.HasValue)
        {
            var start = issueFrom.Value.Date;
            query = query.Where(c => c.IssueDate >= start);
        }

        if (issueTo.HasValue)
        {
            var end = issueTo.Value.Date.AddDays(1);
            query = query.Where(c => c.IssueDate < end);
        }

        return query;
    }

    private async Task<IActionResult> BuildPdfExportAsync(
        List<Certificate> certificates, CertificateExportSettings settings, string scope, bool download = false)
    {
        var bytes = await _pdfService.BuildAsync(certificates, BuildVerificationUrl, settings);

        var prefix = string.IsNullOrWhiteSpace(settings.FileNamePrefix) ? "certificates" : settings.FileNamePrefix!.Trim();
        var fileName = $"{prefix}-{scope}-{DateTime.Now:yyyyMMdd-HHmm}.pdf";

        await _audit.LogAsync(
            User.Identity?.Name ?? "Public",
            "Export",
            "Certificates",
            null,
            null,
            $"PDF export ({scope}): {certificates.Count} certificate(s)");

        return File(bytes, "application/pdf", fileName);
    }

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Verify()
    {
        ViewBag.TurnstileSiteKey = _turnstile.SiteKey;
        return View();
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("public-verification")]
    public async Task<IActionResult> Verify(string code)
    {
        if (!IsVerifyRequestAllowed())
            return BuildRateLimitedVerifyResult(code);

        if (!await _turnstile.VerifyRequestAsync(HttpContext))
            return BuildInvalidVerifyResult(code);

        var normalizedCode = NormalizeLookupCode(code);
        if (string.IsNullOrWhiteSpace(normalizedCode))
            return BuildInvalidVerifyResult(code);

        var certificate = await _db.Certificates
            .AsNoTracking()
            .Include(c => c.Registration)
            .ThenInclude(r => r!.Course)
            .ThenInclude(course => course!.Partners)
            .FirstOrDefaultAsync(c => c.CertificateNumber == normalizedCode || c.VerificationCode == normalizedCode);

        return await BuildVerifyResultAsync(certificate, code);
    }

    [AllowAnonymous]
    [HttpGet("/v/{publicId}")]
    [EnableRateLimiting("public-verification")]
    public async Task<IActionResult> PublicVerify(string publicId, string? sig)
    {
        if (!IsVerifyRequestAllowed())
            return BuildRateLimitedVerifyResult(publicId);

        if (string.IsNullOrWhiteSpace(publicId) || !_certificateSecurity.VerifySignature(publicId, sig))
            return BuildInvalidVerifyResult(publicId);

        var normalizedPublicId = publicId.Trim();
        var certificate = await _db.Certificates
            .AsNoTracking()
            .Include(c => c.Registration)
            .ThenInclude(r => r!.Course)
            .ThenInclude(course => course!.Partners)
            .FirstOrDefaultAsync(c => c.PublicId == normalizedPublicId);

        return await BuildVerifyResultAsync(certificate, normalizedPublicId);
    }

    private FileResult BuildExcelExport(List<Certificate> certificates, string scope)
    {
        using var export = new ExcelExport("الشهادات",
            "رقم الشهادة", "اسم الطالب (العربية)", "اسم الطالب (English)", "الدورة (English)",
            "الدورة (العربية)", "تاريخ الإصدار", "الحالة", "المصدر", "المعرف العام", "رمز التحقق", "رابط QR");

        foreach (var certificate in certificates)
        {
            export.WriteRow(
                certificate.CertificateNumber,
                certificate.TraineeNameArabic ?? certificate.TraineeName,
                certificate.TraineeNameEnglish ?? certificate.TraineeName,
                certificate.CourseNameEnglish,
                certificate.CourseNameArabic,
                certificate.IssueDate.ToString("yyyy-MM-dd"),
                ExcelLabels.CertificateStatus(certificate.Status),
                string.Equals(certificate.Source, "Legacy", StringComparison.OrdinalIgnoreCase) ? "قديمة (مستوردة)" : "نظام",
                certificate.PublicId,
                certificate.VerificationCode,
                BuildVerificationUrl(certificate));
        }

        return export.Build("certificates", scope);
    }

    private async Task<IActionResult> BuildVerifyResultAsync(Certificate? certificate, string? searchCode)
    {
        var model = new PublicCertificateVerificationViewModel
        {
            SearchCode = searchCode?.Trim() ?? string.Empty
        };

        if (certificate == null)
        {
            model.IsValid = false;
            model.Message = InvalidVerifyMessage;
            return View("VerifyResult", model);
        }

        SetCertificateShareImage(certificate);

        model.CertificateFound = true;
        model.IsValid = string.Equals(certificate.Status, "Valid", StringComparison.OrdinalIgnoreCase);
        model.Message = model.IsValid
            ? "تم العثور على شهادة سارية."
            : string.Equals(certificate.Status, "Revoked", StringComparison.OrdinalIgnoreCase)
                ? "تم العثور على الشهادة، لكنها ملغاة ولا يمكن اعتمادها."
                : "تم العثور على الشهادة، لكن حالتها غير معروفة. يرجى مراجعة الجهة المصدرة.";
        model.CertificateNumber = certificate.CertificateNumber;
        model.TraineeName = certificate.TraineeName;
        model.TraineeNameArabic = certificate.TraineeNameArabic ?? certificate.TraineeName;
        model.TraineeNameEnglish = certificate.TraineeNameEnglish ?? certificate.TraineeName;
        model.CourseName = certificate.CourseNameArabic;
        model.CourseNameEnglish = certificate.CourseNameEnglish;
        model.CourseNameArabic = certificate.CourseNameArabic;
        model.IssueDate = certificate.IssueDate;
        model.Status = MapCertificateStatus(certificate.Status);

        var design = await _renderService.BuildAsync(certificate, BuildVerificationUrl(certificate));
        if (design.HasDesign)
        {
            model.HasPublishedDesign = true;
            model.DesignCanvasWidth = design.CanvasWidth;
            model.DesignCanvasHeight = design.CanvasHeight;
            model.DesignBackgroundColor = design.BackgroundColor;
            model.DesignBorderColor = design.BorderColor;
            model.DesignBorderWidth = design.BorderWidth;
            model.DesignBorderRadius = design.BorderRadius;
            model.DesignBackgroundImageUrl = design.BackgroundImageUrl;
            model.DesignElements = design.Elements.ToList();
        }

        // Public PDF download is opt-in per deployment; expose the signed link only when enabled.
        var exportSettings = await _exportSettings.GetAsync();
        if (exportSettings.AllowPublicDownload && !string.IsNullOrWhiteSpace(certificate.PublicId))
        {
            model.AllowPublicDownload = true;
            var signature = _certificateSecurity.ComputeSignature(certificate.PublicId);
            model.PublicPdfUrl = Url.Action(nameof(PublicPdf), new
            {
                publicId = certificate.PublicId,
                sig = signature
            });
        }

        return View("VerifyResult", model);
    }

    private IActionResult BuildInvalidVerifyResult(string? searchCode)
    {
        var model = new PublicCertificateVerificationViewModel
        {
            IsValid = false,
            SearchCode = searchCode?.Trim() ?? string.Empty,
            Message = InvalidVerifyMessage
        };

        return View("VerifyResult", model);
    }

    private IActionResult BuildRateLimitedVerifyResult(string? searchCode)
    {
        Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var model = new PublicCertificateVerificationViewModel
        {
            IsValid = false,
            SearchCode = searchCode?.Trim() ?? string.Empty,
            Message = "تم تجاوز عدد المحاولات المسموح بها. حاول مرة أخرى لاحقاً."
        };

        return View("VerifyResult", model);
    }

    private string BuildVerificationUrl(Certificate certificate)
        => _certificateSecurity.BuildVerificationUrl(certificate.PublicId, GetPublicBaseUrl());

    /// <summary>
    /// Points the social preview at this certificate's share card and fills the description with
    /// the trainee/course summary, so a shared certificate link previews as the certificate itself.
    /// </summary>
    private void SetCertificateShareImage(Certificate certificate)
    {
        if (string.IsNullOrWhiteSpace(certificate.PublicId)) return;

        var signature = _certificateSecurity.ComputeSignature(certificate.PublicId);
        var baseUrl = GetPublicBaseUrl().TrimEnd('/');
        ViewData["OgImage"] = $"{baseUrl}/share/certificate/{Uri.EscapeDataString(certificate.PublicId)}.png?sig={Uri.EscapeDataString(signature)}";

        var trainee = certificate.TraineeNameArabic ?? certificate.TraineeName;
        var course = certificate.CourseNameArabic ?? certificate.CourseName;
        ViewData["OgTitle"] = $"شهادة {trainee} - {course}";
        if (!string.IsNullOrWhiteSpace(trainee) || !string.IsNullOrWhiteSpace(course))
            ViewData["OgDescription"] = $"شهادة صادرة عن {course} للمتدرب {trainee} — رقم {certificate.CertificateNumber}.";
    }

    /// <summary>
    /// Serves the verification QR as a PNG. Used by the certificate list and by the old-system
    /// import report so the QR can be downloaded or embedded anywhere without a data URI.
    /// </summary>
    [AllowAnonymous]
    [HttpGet]
    [EnableRateLimiting("public-verification")]
    public async Task<IActionResult> QrImage(string publicId, string? sig, int size = 8)
    {
        if (!IsVerifyRequestAllowed())
            return StatusCode(StatusCodes.Status429TooManyRequests);

        if (string.IsNullOrWhiteSpace(publicId) || !_certificateSecurity.VerifySignature(publicId, sig))
            return NotFound();

        var exists = await _db.Certificates.AsNoTracking().AnyAsync(c => c.PublicId == publicId.Trim());
        if (!exists) return NotFound();

        var url = _certificateSecurity.BuildVerificationUrl(publicId.Trim(), GetPublicBaseUrl());
        return File(_qrService.BuildPng(url, size), "image/png");
    }

    private string GetPublicBaseUrl()
    {
        var configured = _configuration["PUBLIC_BASE_URL"] ?? _configuration["CertificateSecurity:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return $"{Request.Scheme}://{Request.Host}";
    }

    private bool IsVerifyRequestAllowed() => _verifyGuard.IsAllowed(HttpContext);


    private async Task<string> GenerateUniqueCertificateNumberAsync(string certificatePrefix, HashSet<string>? reservedValues = null)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var value = _certificateSecurity.GenerateCertificateNumber(certificatePrefix);
            if (reservedValues != null && reservedValues.Contains(value))
                continue;

            var exists = await _db.Certificates.AnyAsync(c => c.CertificateNumber == value);
            if (exists) continue;

            reservedValues?.Add(value);
            return value;
        }

        throw new InvalidOperationException("تعذر توليد رقم شهادة فريد.");
    }

    private async Task<string> GenerateUniquePublicIdAsync(HashSet<string>? reservedValues = null)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var value = _certificateSecurity.GeneratePublicId();
            if (reservedValues != null && reservedValues.Contains(value))
                continue;

            var exists = await _db.Certificates.AnyAsync(c => c.PublicId == value);
            if (exists) continue;

            reservedValues?.Add(value);
            return value;
        }

        throw new InvalidOperationException("تعذر توليد معرف عام فريد.");
    }

    private async Task<string> GenerateUniqueVerificationCodeAsync(HashSet<string>? reservedValues = null)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var value = _certificateSecurity.GenerateVerificationCode();
            if (reservedValues != null && reservedValues.Contains(value))
                continue;

            var exists = await _db.Certificates.AnyAsync(c => c.VerificationCode == value);
            if (exists) continue;

            reservedValues?.Add(value);
            return value;
        }

        throw new InvalidOperationException("تعذر توليد رمز تحقق فريد.");
    }

    private static string NormalizeLookupCode(string? value)
        => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string MapCertificateStatus(string? status) => ExcelLabels.CertificateStatus(status);

}
