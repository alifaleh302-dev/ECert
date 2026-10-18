using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ECert.Models;

public class Course
{
    [Key]
    public int CourseId { get; set; }

    [StringLength(200)]
    [Display(Name = "اسم الدورة القديم")]
    public string CourseName { get; set; } = string.Empty;

    [Required(ErrorMessage = "اسم الدورة بالإنجليزية مطلوب")]
    [StringLength(200)]
    [Display(Name = "اسم الدورة بالإنجليزية")]
    public string? CourseNameEnglish { get; set; }

    [Required(ErrorMessage = "اسم الدورة بالعربية مطلوب")]
    [StringLength(200)]
    [Display(Name = "اسم الدورة بالعربية")]
    public string? CourseNameArabic { get; set; }

    [Display(Name = "الصورة")]
    public string? ImageUrl { get; set; }

    [Display(Name = "الوصف المختصر")]
    public string? ShortDescription { get; set; }

    [Display(Name = "الوصف الكامل")]
    public string? FullDescription { get; set; }

    [Display(Name = "أهداف الدورة")]
    public string? Objectives { get; set; }

    [Display(Name = "محتوى الدورة")]
    public string? Content { get; set; }

    [Required(ErrorMessage = "الفئة مطلوبة")]
    public int CategoryId { get; set; }
    public Category? Category { get; set; }

    [Required(ErrorMessage = "المدرب الرئيسي مطلوب")]
    public int InstructorId { get; set; }
    public Instructor? Instructor { get; set; }

    [Display(Name = "تاريخ البداية")]
    [DataType(DataType.Date)]
    public DateTime? StartDate { get; set; }

    [Display(Name = "تاريخ النهاية")]
    [DataType(DataType.Date)]
    public DateTime? EndDate { get; set; }

    [Display(Name = "المكان")]
    public string? Location { get; set; }

    /// <summary>
    /// Public-facing duration derived from the stored date range; dates remain available to administration.
    /// </summary>
    [NotMapped]
    public string DurationText => CourseDurationFormatter.Format(StartDate, EndDate);

    [Display(Name = "السعر")]
    [Range(0, double.MaxValue, ErrorMessage = "السعر يجب أن يكون موجباً")]
    public decimal Price { get; set; }

    [Display(Name = "نوع الخصم")]
    public string? DiscountType { get; set; }

    [Display(Name = "قيمة الخصم")]
    [Range(0, double.MaxValue, ErrorMessage = "قيمة الخصم يجب أن تكون موجبة")]
    public decimal DiscountValue { get; set; } = 0;

    [Display(Name = "المقاعد الإجمالية")]
    public int TotalSeats { get; set; }

    [Display(Name = "المقاعد المحجوزة")]
    public int BookedSeats { get; set; }

    public decimal FinalPrice => DiscountType switch
    {
        "Percentage" => Price - (Price * DiscountValue / 100),
        "Fixed" => Math.Max(0, Price - DiscountValue),
        _ => Price
    };

    [Display(Name = "قالب الشهادة")]
    public int? CertificateDesignId { get; set; }
    public CertificateDesign? CertificateDesign { get; set; }

    [Display(Name = "حالة الدورة")]
    [StringLength(40)]
    public string Status { get; set; } = "Draft";

    public bool IsFeatured { get; set; } = false;

    [Display(Name = "دورة جامعية")]
    public bool RequiresAcademicDetails { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ICollection<Registration> Registrations { get; set; } = new List<Registration>();
    public ICollection<CourseInstructor> CourseInstructors { get; set; } = new List<CourseInstructor>();

    [NotMapped]
    public List<int> SelectedInstructorIds { get; set; } = new();

    [NotMapped]
    public IReadOnlyList<Instructor> AssignedInstructors
    {
        get
        {
            var assigned = CourseInstructors
                .Where(ci => ci.Instructor != null)
                .OrderBy(ci => ci.SortOrder)
                .ThenBy(ci => ci.CourseInstructorId)
                .Select(ci => ci.Instructor!)
                .GroupBy(i => i.InstructorId)
                .Select(g => g.First())
                .ToList();

            if (assigned.Count > 0)
                return assigned;

            return Instructor == null ? Array.Empty<Instructor>() : new[] { Instructor };
        }
    }

    [NotMapped]
    public Instructor? PrimaryInstructor => AssignedInstructors.FirstOrDefault();

    [NotMapped]
    public string InstructorNamesDisplay => string.Join("، ", AssignedInstructors.Select(i => i.FullName));
}
