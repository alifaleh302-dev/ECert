namespace ECert.Services.Excel;

/// <summary>
/// Arabic labels for the enum-like status strings stored in the database. Every export renders
/// these the same way so a value never appears as raw English in one sheet and Arabic in another.
/// </summary>
public static class ExcelLabels
{
    public static string CourseStatus(string? status) => status switch
    {
        "Draft" => "مسودة",
        "Published" => "منشورة",
        "OpenForRegistration" => "مفتوحة للتسجيل",
        "InProgress" => "جارية",
        "Completed" => "مكتملة",
        "Archived" => "مؤرشفة",
        _ => status ?? string.Empty
    };

    public static string RegistrationStatus(string? status) => status switch
    {
        "Pending" => "قيد المراجعة",
        "Accepted" => "مقبول",
        "Rejected" => "مرفوض",
        "Cancelled" => "ملغي",
        _ => status ?? string.Empty
    };

    public static string CertificateStatus(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "valid" => "سارية",
        "revoked" => "ملغية",
        _ => "غير معروفة"
    };

    public static string Gender(string? gender) => gender switch
    {
        "Male" => "ذكر",
        "Female" => "أنثى",
        _ => string.Empty
    };

    /// <summary>Accepts the Arabic label this class writes or the stored English value.</summary>
    public static string? ParseGender(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        return text switch
        {
            "ذكر" or "Male" or "male" or "M" or "m" => "Male",
            "أنثى" or "انثى" or "Female" or "female" or "F" or "f" => "Female",
            _ => text
        };
    }

    public static string PaymentMethod(string? method) => method switch
    {
        "Cash" => "نقدي",
        "Electronic" => "محفظة إلكترونية",
        "BankTransfer" => "تحويل بنكي",
        "Card" => "بطاقة",
        _ => method ?? string.Empty
    };

    public static string Boolean(bool value) => value ? "نعم" : "لا";

    public static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd") ?? string.Empty;

    /// <summary>Parses a status cell, accepting the Arabic labels this class writes or the stored English name.</summary>
    public static string ParseCourseStatus(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return "Draft";

        foreach (var status in CourseStatuses)
            if (string.Equals(CourseStatus(status), text, StringComparison.OrdinalIgnoreCase))
                return status;

        foreach (var status in CourseStatuses)
            if (string.Equals(status, text, StringComparison.OrdinalIgnoreCase))
                return status;

        return text;
    }

    public static bool IsKnownCourseStatus(string? status)
        => CourseStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);

    public static string? ParseDiscountType(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (text is "نسبة مئوية" or "نسبة" or "Percent" or "percentage") return "Percentage";
        if (text is "مبلغ ثابت" or "ثابت" or "Fixed" or "fixed") return "Fixed";
        return text;
    }

    private static readonly string[] CourseStatuses =
        ["Draft", "Published", "OpenForRegistration", "InProgress", "Completed", "Archived"];
}
