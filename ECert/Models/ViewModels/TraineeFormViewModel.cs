using System.ComponentModel.DataAnnotations;

namespace ECert.Models.ViewModels;

/// <summary>
/// One form for both "add trainee" and "edit trainee" on the registrations screen. The trainee is a
/// <see cref="Registration"/>; the fee snapshot, invoice and certificate stay untouched here — this
/// form only owns the personal, contact, academic and course fields.
/// </summary>
public class TraineeFormViewModel
{
    public int? RegistrationId { get; set; }
    public string? RequestNumber { get; set; }
    public string? Status { get; set; }
    public string? ReturnUrl { get; set; }

    [Required(ErrorMessage = "الاسم بالعربية مطلوب")]
    [StringLength(100)]
    [Display(Name = "الاسم بالعربية")]
    public string? FullNameArabic { get; set; }

    [Required(ErrorMessage = "الاسم بالإنجليزية مطلوب")]
    [StringLength(100)]
    [Display(Name = "الاسم بالإنجليزية")]
    public string? FullNameEnglish { get; set; }

    [Required(ErrorMessage = "رقم الهاتف مطلوب")]
    [StringLength(30)]
    [Display(Name = "رقم الهاتف")]
    public string? Phone { get; set; }

    [Required(ErrorMessage = "الجنس مطلوب")]
    [Display(Name = "الجنس")]
    public string? Gender { get; set; }

    [EmailAddress(ErrorMessage = "صيغة البريد الإلكتروني غير صحيحة")]
    [StringLength(150)]
    [Display(Name = "البريد الإلكتروني")]
    public string? Email { get; set; }

    [StringLength(80)]
    [Display(Name = "كيف سمع عنا؟")]
    public string? HeardFrom { get; set; }

    [Required(ErrorMessage = "اختر الدورة")]
    [Display(Name = "الدورة")]
    public int? CourseId { get; set; }

    [Display(Name = "الجامعة")]
    public int? UniversityId { get; set; }

    [Display(Name = "الكلية")]
    public int? CollegeId { get; set; }

    [Display(Name = "التخصص")]
    public int? AcademicSpecializationId { get; set; }

    [StringLength(80)]
    [Display(Name = "المستوى الدراسي")]
    public string? AcademicLevel { get; set; }

    /// <summary>Create only: accept the trainee right away and issue the invoice.</summary>
    [Display(Name = "اعتماد المتدرب مباشرة وإنشاء الفاتورة")]
    public bool AcceptImmediately { get; set; }

    // Display-only.
    public bool IsEdit => RegistrationId.HasValue;
    public bool CourseLocked { get; set; }
    public string? CourseLockReason { get; set; }
    public string? LockedCourseName { get; set; }
    public bool CourseRequiresAcademicDetails { get; set; }

    public List<Course> Courses { get; set; } = new();
    public List<University> Universities { get; set; } = new();
    public List<College> Colleges { get; set; } = new();
    public List<AcademicSpecialization> Specializations { get; set; } = new();
    public List<AcademicLevelOption> Levels { get; set; } = new();
}
