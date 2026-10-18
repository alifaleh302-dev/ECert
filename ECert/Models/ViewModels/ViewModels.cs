using System.ComponentModel.DataAnnotations;

namespace ECert.Models.ViewModels;

public class LoginViewModel
{
    [Required(ErrorMessage = "اسم المستخدم مطلوب")]
    [Display(Name = "اسم المستخدم")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "كلمة المرور مطلوبة")]
    [Display(Name = "كلمة المرور")]
    public string Password { get; set; } = string.Empty;
}

public class PublicRegistrationViewModel
{
    [Required(ErrorMessage = "الاسم بالعربية مطلوب")]
    [StringLength(100)]
    [Display(Name = "الاسم بالعربية")]
    public string? FullNameArabic { get; set; }

    [Required(ErrorMessage = "الاسم بالإنجليزية مطلوب")]
    [StringLength(100)]
    [Display(Name = "الاسم بالإنجليزية")]
    public string? FullNameEnglish { get; set; }

    [Required(ErrorMessage = "رقم الهاتف مطلوب")]
    [Display(Name = "رقم الهاتف")]
    public string Phone { get; set; } = string.Empty;

    [Required(ErrorMessage = "الجنس مطلوب")]
    [Display(Name = "الجنس")]
    public string? Gender { get; set; }

    [Required(ErrorMessage = "الدولة مطلوبة")]
    [Display(Name = "الدولة")]
    public int CountryId { get; set; }

    [Display(Name = "البريد الإلكتروني (اختياري)")]
    [EmailAddress(ErrorMessage = "البريد الإلكتروني غير صحيح")]
    public string? Email { get; set; }

    [Display(Name = "كيف سمعت عنا؟ (اختياري)")]
    public string? HeardFrom { get; set; }

    [Display(Name = "إدخال البيانات الجامعية")]
    public bool IncludeAcademicDetails { get; set; }

    [Display(Name = "الجامعة")]
    public int? UniversityId { get; set; }

    [Display(Name = "الكلية")]
    public int? CollegeId { get; set; }

    [Display(Name = "التخصص")]
    public int? AcademicSpecializationId { get; set; }

    [Display(Name = "المستوى الدراسي")]
    public string? AcademicLevel { get; set; }

    // حقول عرض فقط؛ تُعاد تعبئتها من الخادم ولا يُعتمد عليها عند الحفظ.
    public string? UniversityName { get; set; }
    public string? CollegeName { get; set; }
    public string? SpecializationName { get; set; }

    // Honeypot: must remain empty for real visitors.
    public string? WebsiteUrl { get; set; }

    public int CourseId { get; set; }
    public string? CourseName { get; set; }
    public string? CourseNameEnglish { get; set; }
    public string? CourseNameArabic { get; set; }
}

public class DashboardViewModel
{
    public int PendingRegistrations { get; set; }
    public int UnpaidInvoices { get; set; }
    public int ActiveCourses { get; set; }
    public int NewPosts { get; set; }
    public decimal TotalRevenue { get; set; }
    public int TotalTrainees { get; set; }
    public List<Registration> RecentRegistrations { get; set; } = new();
    public List<Invoice> RecentInvoices { get; set; } = new();
}

public class RejectRegistrationViewModel
{
    public int RegistrationId { get; set; }
    public string? ReturnUrl { get; set; }
    [Required(ErrorMessage = "سبب الرفض مطلوب")]
    [Display(Name = "سبب الرفض")]
    public string Reason { get; set; } = string.Empty;
}

public class ApplyExemptionViewModel
{
    public int RegistrationId { get; set; }

    [Display(Name = "مبلغ الإعفاء")]
    [Range(0, double.MaxValue, ErrorMessage = "مبلغ الإعفاء لا يمكن أن يكون سالباً")]
    public decimal ExemptionAmount { get; set; }

    [StringLength(500, ErrorMessage = "سبب الإعفاء لا يمكن أن يتجاوز 500 حرف")]
    [Display(Name = "سبب الإعفاء")]
    public string? Reason { get; set; }
}

public class AddPaymentViewModel
{
    public int InvoiceId { get; set; }
    [Required(ErrorMessage = "المبلغ مطلوب")]
    [Range(0.01, double.MaxValue, ErrorMessage = "المبلغ يجب أن يكون أكبر من صفر")]
    [Display(Name = "المبلغ")]
    public decimal Amount { get; set; }

    [Required(ErrorMessage = "طريقة الدفع مطلوبة")]
    [Display(Name = "طريقة الدفع")]
    public string PaymentMethod { get; set; } = "Cash";

    [Display(Name = "رقم السند / رقم العملية")]
    public string? ReferenceNumber { get; set; }

    [Display(Name = "ملاحظات")]
    public string? Notes { get; set; }
}

public class CreateUserViewModel
{
    [Required(ErrorMessage = "اسم المستخدم مطلوب")]
    [Display(Name = "اسم المستخدم")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "الاسم الكامل مطلوب")]
    [Display(Name = "الاسم الكامل")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "البريد الإلكتروني مطلوب")]
    [EmailAddress]
    [Display(Name = "البريد الإلكتروني")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "رقم الهاتف")]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "كلمة المرور مطلوبة")]
    [Display(Name = "كلمة المرور")]
    public string Password { get; set; } = string.Empty;

    [Display(Name = "الدور")]
    public int RoleId { get; set; }
}

public class ReportsViewModel
{
    public decimal TotalRevenue { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal TotalRemaining { get; set; }
    public decimal TotalDiscounts { get; set; }
    public int UnpaidCount { get; set; }
    public int PaidCount { get; set; }
    public List<Invoice> Invoices { get; set; } = new();
    public string Period { get; set; } = "month";
}

public class CertificateIssuePageViewModel
{
    public int? CourseId { get; set; }
    public DateTime? RegistrationFrom { get; set; }
    public DateTime? RegistrationTo { get; set; }
    public List<Course> Courses { get; set; } = new();
    public List<Registration> EligibleRegistrations { get; set; } = new();
}

public class CertificateIndexPageViewModel
{
    public string? Search { get; set; }
    public int? CourseId { get; set; }
    public DateTime? IssueFrom { get; set; }
    public DateTime? IssueTo { get; set; }
    public List<Course> Courses { get; set; } = new();
    public List<Certificate> Certificates { get; set; } = new();
    public Dictionary<int, string> VerificationUrls { get; set; } = new();
}

public class PublicCertificateVerificationViewModel
{
    public bool IsValid { get; set; }
    public bool CertificateFound { get; set; }
    public string SearchCode { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? CertificateNumber { get; set; }
    public string? TraineeName { get; set; }
    public string? TraineeNameArabic { get; set; }
    public string? TraineeNameEnglish { get; set; }
    public string? CourseName { get; set; }
    public string? CourseNameEnglish { get; set; }
    public string? CourseNameArabic { get; set; }
    public DateTime? IssueDate { get; set; }
    public string? Status { get; set; }

    // القيم أدناه لا تمثل بيانات الشهادة الأصلية؛ هي إعدادات عرض مُطبّعة لخدمة التحقق العامة.
    public bool HasPublishedDesign { get; set; }
    public int DesignCanvasWidth { get; set; } = 1120;
    public int DesignCanvasHeight { get; set; } = 792;
    public string DesignBackgroundColor { get; set; } = "#fffdf7";
    public string DesignBorderColor { get; set; } = "#c9a227";
    public int DesignBorderWidth { get; set; } = 12;
    public int DesignBorderRadius { get; set; } = 8;
    public List<PublicCertificateDesignElementViewModel> DesignElements { get; set; } = new();
}

public class RoleFormViewModel
{
    [Required(ErrorMessage = "اسم الدور مطلوب")]
    [StringLength(50, ErrorMessage = "اسم الدور لا يمكن أن يتجاوز 50 حرفاً")]
    [Display(Name = "اسم الدور")]
    public string RoleName { get; set; } = string.Empty;

    [StringLength(250, ErrorMessage = "الوصف لا يمكن أن يتجاوز 250 حرفاً")]
    [Display(Name = "وصف الدور")]
    public string? Description { get; set; }

    [MinLength(1, ErrorMessage = "اختر صلاحية واحدة على الأقل لهذا الدور")]
    public List<int> PermissionIds { get; set; } = new();
}

public class CertificateDesignEditorViewModel
{
    public CertificateDesign Design { get; set; } = new();
    public List<CertificateDesign> Designs { get; set; } = new();
    public IReadOnlyDictionary<string, string> FieldLabels { get; set; } = new Dictionary<string, string>();
}

public class CertificateDesignEditorPostModel
{
    public int CertificateDesignId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CanvasWidth { get; set; }
    public int CanvasHeight { get; set; }
    public string BackgroundColor { get; set; } = "#fffdf7";
    public string BorderColor { get; set; } = "#c9a227";
    public int BorderWidth { get; set; }
    public int BorderRadius { get; set; }
    public bool Publish { get; set; }
    public string ElementsJson { get; set; } = "[]";
}

public class PublicCertificateDesignElementViewModel
{
    public string ElementType { get; set; } = "text";
    public string Content { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FontSize { get; set; }
    public string FontFamily { get; set; } = "Tajawal";
    public string FontColor { get; set; } = "#172033";
    public string FontWeight { get; set; } = "600";
    public string TextAlign { get; set; } = "center";
    public int ZIndex { get; set; }
    public int Rotation { get; set; }
}

public class CourseRevenueRowViewModel
{
    public int CourseId { get; set; }
    public string CourseName { get; set; } = string.Empty;
    public int Registrations { get; set; }
    public decimal Original { get; set; }
    public decimal Exemptions { get; set; }
    public decimal Expected { get; set; }
    public decimal Paid { get; set; }
    public decimal Remaining { get; set; }
    public int FullPaid { get; set; }
    public int Partial { get; set; }
    public int Unpaid { get; set; }
    public decimal Cash { get; set; }
    public decimal Bank { get; set; }
    public decimal Transferred { get; set; }
    public decimal AvailableToTransfer { get; set; }
}
