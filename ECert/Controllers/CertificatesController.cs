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
    private readonly CertificateDesignService _certificateDesignService;

    public CertificatesController(
        ECertDbContext db,
        AuditLogService audit,
        NotificationService notify,
        CertificateSecurityService certificateSecurity,
        VerifyRequestGuardService verifyGuard,
        IConfiguration configuration,
        TurnstileService turnstile,
        CertificateDesignService certificateDesignService)
    {
        _db = db;
        _audit = audit;
        _notify = notify;
        _certificateSecurity = certificateSecurity;
        _verifyGuard = verifyGuard;
        _configuration = configuration;
        _turnstile = turnstile;
        _certificateDesignService = certificateDesignService;
    }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    [HttpGet]
    public async Task<IActionResult> Index(string? search, int? courseId, DateTime? issueFrom, DateTime? issueTo)
    {
        if (!HasPermission("issue-certificates")) return Forbid();

        var query = _db.Certificates
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
            Courses = await _db.Courses.OrderBy(c => c.CourseNameArabic).ToListAsync(),
            Certificates = certificates,
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
            Courses = await _db.Courses.Where(c => c.Status == "Completed").OrderBy(c => c.CourseNameArabic).ToListAsync(),
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
        if (!HasPermission("issue-certificates")) return Forbid();

        var ids = certificateIds?.Where(id => id > 0).Distinct().ToArray() ?? Array.Empty<int>();
        var idList = ids.ToList();
        if (idList.Count == 0)
        {
            TempData["Error"] = "الرجاء تحديد شهادة واحدة على الأقل للتصدير.";
            return RedirectToAction(nameof(Index));
        }

        var certificates = await _db.Certificates
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
        if (!HasPermission("issue-certificates")) return Forbid();

        var query = _db.Certificates
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
            .FirstOrDefaultAsync(c => c.PublicId == normalizedPublicId);

        return await BuildVerifyResultAsync(certificate, normalizedPublicId);
    }

    private FileResult BuildExcelExport(List<Certificate> certificates, string scope)
    {
        using var export = new ExcelExport("الشهادات",
            "رقم الشهادة", "اسم الطالب (العربية)", "اسم الطالب (English)", "الدورة (English)",
            "الدورة (العربية)", "تاريخ الإصدار", "الحالة", "المعرف العام", "رمز التحقق", "رابط QR");

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

        var design = await _certificateDesignService.GetForCourseAsync(certificate.Registration?.CourseId);
        if (design != null)
        {
            model.HasPublishedDesign = true;
            model.DesignCanvasWidth = design.CanvasWidth;
            model.DesignCanvasHeight = design.CanvasHeight;
            model.DesignBackgroundColor = design.BackgroundColor;
            model.DesignBorderColor = design.BorderColor;
            model.DesignBorderWidth = design.BorderWidth;
            model.DesignBorderRadius = design.BorderRadius;
            var verificationUrl = BuildVerificationUrl(certificate);
            model.DesignElements = design.Elements
                .Where(e => e.IsVisible)
                .OrderBy(e => e.SortOrder)
                .Select(e => new PublicCertificateDesignElementViewModel
                {
                    ElementType = e.ElementType,
                    Content = string.Equals(e.ElementType, "field", StringComparison.OrdinalIgnoreCase)
                        ? CertificateDesignService.ResolveFieldValue(e.FieldKey, certificate, verificationUrl)
                        : e.Content,
                    X = e.X,
                    Y = e.Y,
                    Width = e.Width,
                    Height = e.Height,
                    FontSize = e.FontSize,
                    FontFamily = e.FontFamily,
                    FontColor = e.FontColor,
                    FontWeight = e.FontWeight,
                    TextAlign = e.TextAlign,
                    ZIndex = e.ZIndex,
                    Rotation = e.Rotation
                })
                .ToList();
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
