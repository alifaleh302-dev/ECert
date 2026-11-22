using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

/// <summary>
/// A snapshot of a course fee as the applicant saw it. Snapshotting keeps historical invoices
/// stable when the course's fee definitions are edited or removed later.
/// </summary>
public class RegistrationFee
{
    [Key]
    public int RegistrationFeeId { get; set; }

    [Required]
    public int RegistrationId { get; set; }
    public Registration? Registration { get; set; }

    /// <summary>The course fee this row was created from; null for an ad-hoc registration charge.</summary>
    public int? CourseFeeId { get; set; }
    public CourseFee? CourseFee { get; set; }

    [Required]
    [StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [Required]
    [StringLength(30)]
    public string FeeType { get; set; } = CourseFeeTypes.Certificate;

    [StringLength(500)]
    public string? Description { get; set; }

    public decimal Amount { get; set; }

    public bool IsOptional { get; set; }

    /// <summary>False keeps the historical choice without charging it.</summary>
    public bool IsSelected { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
