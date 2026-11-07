using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Keeps the visual certificate designer separate from the historical certificate settings and
/// strictly limits every value that can reach the public certificate view.
/// </summary>
public sealed class CertificateDesignService
{
    public static readonly IReadOnlyDictionary<string, string> FieldLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["certificate_number"] = "رقم الشهادة",
            ["trainee_name"] = "اسم المتدرب",
            ["trainee_name_arabic"] = "اسم المتدرب بالعربية",
            ["trainee_name_english"] = "اسم المتدرب بالإنجليزية",
            ["course_name"] = "اسم الدورة",
            ["course_name_arabic"] = "اسم الدورة بالعربية",
            ["course_name_english"] = "اسم الدورة بالإنجليزية",
            ["issue_date"] = "تاريخ الإصدار",
            ["issued_by"] = "الجهة المصدرة",
            ["status"] = "الحالة",
            ["verification_code"] = "رمز التحقق",
            ["verification_url"] = "رابط التحقق"
        };

    public static readonly IReadOnlySet<string> AllowedElementTypes =
        new HashSet<string>(new[] { "text", "field", "divider", "qr" }, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> AllowedFontFamilies =
        new HashSet<string>(new[] { "Tajawal", "Arial", "Tahoma", "Georgia", "Courier New" }, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> AllowedFontWeights =
        new HashSet<string>(new[] { "400", "500", "600", "700", "800" }, StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> AllowedTextAlignments =
        new HashSet<string>(new[] { "right", "center", "left" }, StringComparer.OrdinalIgnoreCase);

    private readonly ECertDbContext _db;

    public CertificateDesignService(ECertDbContext db) => _db = db;

    public Task<CertificateDesign?> GetPublishedAsync()
        => _db.CertificateDesigns
            .AsNoTracking()
            .Include(t => t.Elements)
            .Where(t => t.IsPublished)
            .OrderByDescending(t => t.UpdatedAt)
            .FirstOrDefaultAsync();

    public async Task<CertificateDesign?> GetForCourseAsync(int? courseId)
    {
        if (courseId.HasValue)
        {
            var assignedDesignId = await _db.Courses
                .AsNoTracking()
                .Where(c => c.CourseId == courseId.Value)
                .Select(c => c.CertificateDesignId)
                .FirstOrDefaultAsync();

            if (assignedDesignId.HasValue)
            {
                var assigned = await _db.CertificateDesigns
                    .AsNoTracking()
                    .Include(t => t.Elements)
                    .FirstOrDefaultAsync(t => t.CertificateDesignId == assignedDesignId.Value);
                if (assigned != null)
                    return assigned;
            }
        }

        return await GetPublishedAsync();
    }

    public static CertificateDesign CreateDefault(string? updatedBy)
    {
        var now = DateTime.Now;
        var design = new CertificateDesign
        {
            Name = "القالب البصري الأساسي",
            DesignKey = "default",
            IsPublished = true,
            CanvasWidth = 1120,
            CanvasHeight = 792,
            BackgroundColor = "#fffdf7",
            BorderColor = "#c9a227",
            BorderWidth = 12,
            BorderRadius = 8,
            UpdatedBy = updatedBy,
            CreatedAt = now,
            UpdatedAt = now
        };

        design.Elements = new List<CertificateDesignElement>
        {
            Element("text", "", "شهادة إتمام", 160, 92, 800, 72, 44, "#8c6a1c", "800", 0),
            Element("text", "", "تشهد الجهة المصدرة بإتمام المتدرب للبرنامج التدريبي التالي", 180, 185, 760, 42, 18, "#475569", "500", 1),
            Element("field", "trainee_name", "", 130, 265, 860, 74, 34, "#172033", "800", 2),
            Element("text", "", "بنجاح وإتقان", 300, 345, 520, 36, 18, "#64748b", "500", 3),
            Element("field", "course_name", "", 120, 400, 880, 66, 28, "#1d4ed8", "700", 4),
            Element("field", "issue_date", "", 190, 505, 360, 42, 18, "#334155", "600", 5),
            Element("field", "certificate_number", "", 570, 505, 360, 42, 18, "#334155", "600", 6),
            Element("field", "status", "", 350, 590, 420, 42, 17, "#15803d", "700", 7),
            Element("field", "verification_code", "", 280, 665, 560, 34, 14, "#64748b", "500", 8),
            Element("qr", "", "", 900, 610, 150, 150, 14, "#172033", "600", 9)
        };

        return design;
    }

    public static string ResolveFieldValue(string key, Certificate certificate, string verificationUrl)
        => key.Trim().ToLowerInvariant() switch
        {
            "certificate_number" => certificate.CertificateNumber ?? string.Empty,
            "trainee_name" => certificate.TraineeName ?? string.Empty,
            "trainee_name_arabic" => certificate.TraineeNameArabic ?? certificate.TraineeName,
            "trainee_name_english" => certificate.TraineeNameEnglish ?? certificate.TraineeName,
            "course_name" => certificate.CourseNameArabic ?? certificate.CourseName,
            "course_name_arabic" => certificate.CourseNameArabic ?? certificate.CourseName,
            "course_name_english" => certificate.CourseNameEnglish ?? certificate.CourseName,
            "issue_date" => certificate.IssueDate.ToString("yyyy/MM/dd"),
            "issued_by" => certificate.IssuedBy ?? string.Empty,
            "status" => MapStatus(certificate.Status),
            "verification_code" => certificate.VerificationCode ?? string.Empty,
            "verification_url" => verificationUrl,
            _ => string.Empty
        };

    public static bool IsValidElementType(string value)
        => AllowedElementTypes.Contains(value?.Trim() ?? string.Empty);

    public static bool IsValidFieldKey(string value)
        => !string.IsNullOrWhiteSpace(value) && FieldLabels.ContainsKey(value.Trim());

    public static string NormalizeColor(string? value, string fallback)
    {
        var candidate = (value ?? string.Empty).Trim();
        return candidate.Length == 7 && candidate[0] == '#' && candidate.Skip(1).All(Uri.IsHexDigit)
            ? candidate.ToLowerInvariant()
            : fallback;
    }

    public static string NormalizeFontFamily(string? value)
        => AllowedFontFamilies.Contains(value?.Trim() ?? string.Empty) ? value!.Trim() : "Tajawal";

    public static string NormalizeFontWeight(string? value)
        => AllowedFontWeights.Contains(value?.Trim() ?? string.Empty) ? value!.Trim() : "600";

    public static string NormalizeTextAlign(string? value)
        => AllowedTextAlignments.Contains(value?.Trim() ?? string.Empty) ? value!.Trim() : "center";

    private static CertificateDesignElement Element(
        string type, string fieldKey, string content, int x, int y, int width, int height,
        int fontSize, string color, string weight, int order)
        => new()
        {
            ElementType = type,
            FieldKey = fieldKey,
            Content = content,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            FontSize = fontSize,
            FontFamily = "Tajawal",
            FontColor = color,
            FontWeight = weight,
            TextAlign = "center",
            IsVisible = true,
            SortOrder = order,
            ZIndex = order
        };

    private static string MapStatus(string? status)
        => status?.Trim().ToLowerInvariant() switch
        {
            "valid" => "سارية ومعتمدة",
            "revoked" => "ملغاة",
            _ => "قيد المراجعة"
        };
}
