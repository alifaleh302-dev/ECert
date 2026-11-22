using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

/// <summary>The only extra charge supported by the product: an optional certificate fee.</summary>
public static class CourseFeeTypes
{
    public const string Certificate = "Certificate";

    public static bool IsCertificate(string? value) =>
        string.Equals(value, Certificate, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An optional certificate fee attached to a course, independent of its base price. At most one
/// active certificate fee may exist per course, and selecting it is what unlocks certificate
/// issuance once the invoice is settled.
/// </summary>
public class CourseFee
{
    [Key]
    public int CourseFeeId { get; set; }

    [Required]
    public int CourseId { get; set; }
    public Course? Course { get; set; }

    [Required]
    [StringLength(150)]
    [Display(Name = "اسم الرسم")]
    public string Name { get; set; } = "رسوم الشهادة";

    [Required]
    [StringLength(30)]
    [Display(Name = "نوع الرسم")]
    public string FeeType { get; set; } = CourseFeeTypes.Certificate;

    [StringLength(500)]
    [Display(Name = "وصف مختصر")]
    public string? Description { get; set; }

    [Display(Name = "المبلغ")]
    [Range(0, double.MaxValue, ErrorMessage = "المبلغ يجب أن يكون موجباً")]
    public decimal Amount { get; set; }

    /// <summary>Kept for schema compatibility; the certificate fee is always optional.</summary>
    [Display(Name = "اختياري")]
    public bool IsOptional { get; set; } = true;

    [Display(Name = "نشط")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "الترتيب")]
    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
