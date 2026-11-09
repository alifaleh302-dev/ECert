using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

/// <summary>
/// A supporting organisation credited on a course certificate. Each partner carries its own logo
/// and signature, so a course can thank several organisations at once.
/// </summary>
public class CoursePartner
{
    [Key]
    public int CoursePartnerId { get; set; }

    public int CourseId { get; set; }
    public Course? Course { get; set; }

    [Required(ErrorMessage = "اسم الجهة مطلوب")]
    [StringLength(150)]
    [Display(Name = "اسم الجهة الداعمة")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional logo (transparent PNG recommended) shown wherever a <c>partner_logo</c> element sits.</summary>
    [StringLength(400)]
    public string? LogoUrl { get; set; }

    /// <summary>Optional signature of the partner's authorised signatory.</summary>
    [StringLength(400)]
    public string? SignatureUrl { get; set; }

    public int SortOrder { get; set; }
}
