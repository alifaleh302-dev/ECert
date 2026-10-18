using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

public class Registration
{
    [Key]
    public int RegistrationId { get; set; }

    [Display(Name = "رقم الطلب")]
    public string RequestNumber { get; set; } = string.Empty;

    [Required(ErrorMessage = "الاسم الكامل مطلوب")]
    [StringLength(100)]
    [Display(Name = "الاسم الكامل")]
    public string FullName { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "الاسم بالعربية")]
    public string? FullNameArabic { get; set; }

    [StringLength(100)]
    [Display(Name = "الاسم بالإنجليزية")]
    public string? FullNameEnglish { get; set; }

    [StringLength(20)]
    [Display(Name = "الجنس")]
    public string? Gender { get; set; }

    [Required(ErrorMessage = "رقم الهاتف مطلوب")]
    [Display(Name = "رقم الهاتف")]
    public string Phone { get; set; } = string.Empty;

    [Display(Name = "البريد الإلكتروني")]
    public string? Email { get; set; }

    [Display(Name = "كيف سمعت عنا؟")]
    public string? HeardFrom { get; set; }

    [Display(Name = "الجامعة")]
    public int? UniversityId { get; set; }
    public University? University { get; set; }

    [Display(Name = "الكلية")]
    public int? CollegeId { get; set; }
    public College? College { get; set; }

    [Display(Name = "التخصص")]
    public int? AcademicSpecializationId { get; set; }
    public AcademicSpecialization? AcademicSpecialization { get; set; }

    [StringLength(80)]
    [Display(Name = "المستوى الدراسي")]
    public string? AcademicLevel { get; set; }

    [StringLength(160)]
    public string? UniversityNameSnapshot { get; set; }

    [StringLength(160)]
    public string? CollegeNameSnapshot { get; set; }

    [StringLength(160)]
    public string? SpecializationNameSnapshot { get; set; }

    [Required]
    public int CourseId { get; set; }
    public Course? Course { get; set; }

    [Display(Name = "حالة الطلب")]
    [StringLength(40)]
    public string Status { get; set; } = "Pending";
    // Pending, Accepted, Rejected, Archived

    [Display(Name = "تاريخ التسجيل")]
    public DateTime RegistrationDate { get; set; } = DateTime.Now;

    [Display(Name = "تاريخ القبول")]
    public DateTime? AcceptedDate { get; set; }

    [Display(Name = "الموظف المسؤول")]
    public string? ProcessedBy { get; set; }

    [Display(Name = "سبب الرفض")]
    public string? RejectionReason { get; set; }

    [Display(Name = "تاريخ الرفض")]
    public DateTime? RejectedDate { get; set; }

    public bool CertificateIssued { get; set; } = false;

    [Display(Name = "تاريخ إعادة الفتح")]
    public DateTime? ReopenedDate { get; set; }

    [Display(Name = "تاريخ الأرشفة")]
    public DateTime? ArchivedDate { get; set; }

    [Display(Name = "مبلغ الإعفاء")]
    public decimal ExemptionAmount { get; set; } = 0;

    [StringLength(500)]
    [Display(Name = "سبب الإعفاء")]
    public string? ExemptionReason { get; set; }

    [Display(Name = "طبّق الإعفاء بواسطة")]
    public string? ExemptionAppliedBy { get; set; }

    [Display(Name = "تاريخ تطبيق الإعفاء")]
    public DateTime? ExemptionAppliedAt { get; set; }

    public Invoice? Invoice { get; set; }
    public Certificate? Certificate { get; set; }
}
