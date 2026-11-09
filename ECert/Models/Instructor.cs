using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

public class Instructor
{
    [Key]
    public int InstructorId { get; set; }

    [Required(ErrorMessage = "اسم المدرب مطلوب")]
    [StringLength(100)]
    [Display(Name = "اسم المدرب")]
    public string FullName { get; set; } = string.Empty;

    [Display(Name = "الصورة")]
    public string? PhotoUrl { get; set; }

    /// <summary>Hand-drawn signature captured on screen; merged into certificates this instructor signs.</summary>
    [Display(Name = "التوقيع")]
    [StringLength(400)]
    public string? SignatureImageUrl { get; set; }

    [Display(Name = "السيرة الذاتية")]
    public string? Bio { get; set; }

    [Display(Name = "التخصص")]
    public string? Specialization { get; set; }

    [Display(Name = "رقم الهاتف")]
    public string? Phone { get; set; }

    [Display(Name = "البريد الإلكتروني")]
    public string? Email { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public ICollection<Course> Courses { get; set; } = new List<Course>();
    public ICollection<CourseInstructor> CourseInstructors { get; set; } = new List<CourseInstructor>();
}
