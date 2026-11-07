using System.ComponentModel.DataAnnotations;
namespace ECert.Models;

public class Certificate
{
    [Key]
    public int CertificateId { get; set; }

    [Required]
    [StringLength(40)]
    [Display(Name = "رقم الشهادة")]
    public string CertificateNumber { get; set; } = string.Empty;

    [Required]
    [StringLength(32)]
    [Display(Name = "المعرف العام")]
    public string PublicId { get; set; } = string.Empty;

    [Required]
    public int RegistrationId { get; set; }
    public Registration? Registration { get; set; }

    [Required]
    [StringLength(150)]
    [Display(Name = "اسم المتدرب")]
    public string TraineeName { get; set; } = string.Empty;

    [StringLength(150)]
    [Display(Name = "اسم المتدرب بالعربية")]
    public string? TraineeNameArabic { get; set; }

    [StringLength(150)]
    [Display(Name = "اسم المتدرب بالإنجليزية")]
    public string? TraineeNameEnglish { get; set; }

    [StringLength(200)]
    [Display(Name = "اسم الدورة القديم")]
    public string CourseName { get; set; } = string.Empty;

    [StringLength(200)]
    [Display(Name = "اسم الدورة بالإنجليزية")]
    public string? CourseNameEnglish { get; set; }

    [StringLength(200)]
    [Display(Name = "اسم الدورة بالعربية")]
    public string? CourseNameArabic { get; set; }

    [Display(Name = "تاريخ الإصدار")]
    public DateTime IssueDate { get; set; } = DateTime.Now;

    [Required]
    [StringLength(100)]
    [Display(Name = "أصدرها")]
    public string IssuedBy { get; set; } = string.Empty;

    [Required]
    [StringLength(24)]
    [Display(Name = "رمز التحقق")]
    public string VerificationCode { get; set; } = string.Empty;

    [Required]
    [StringLength(20)]
    [Display(Name = "حالة الشهادة")]
    public string Status { get; set; } = "Valid";

    [Display(Name = "تاريخ الإلغاء")]
    public DateTime? RevokedAt { get; set; }

    [StringLength(500)]
    [Display(Name = "سبب الإلغاء")]
    public string? RevokedReason { get; set; }

    [Display(Name = "إصدار التوقيع")]
    public int SignatureVersion { get; set; } = 1;

    /// <summary>
    /// Where the certificate came from: "New" for certificates issued inside the system,
    /// "Legacy" for records imported from the old (Excel-based) system.
    /// </summary>
    [Required]
    [StringLength(20)]
    [Display(Name = "المصدر")]
    public string Source { get; set; } = "New";

    /// <summary>Prefix used when the legacy certificate number was imported; kept for reference and de-duplication.</summary>
    [StringLength(12)]
    [Display(Name = "بادئة رقم الشهادة")]
    public string? CertificatePrefix { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
