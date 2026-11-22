using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

public static class InvoiceItemKinds
{
    public const string Course = "Course";
    public const string Fee = "Fee";
}

/// <summary>
/// One line of an invoice. The invoice total is always derived from these lines minus the
/// exemption, so a free course with an optional certificate fee and a paid course with the same
/// fee share one model.
/// </summary>
public class InvoiceItem
{
    [Key]
    public int InvoiceItemId { get; set; }

    [Required]
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    [Required]
    [StringLength(20)]
    public string Kind { get; set; } = InvoiceItemKinds.Fee;

    /// <summary>Null for the base course line; otherwise a <see cref="CourseFeeTypes"/> value.</summary>
    [StringLength(30)]
    public string? FeeType { get; set; }

    [Required]
    [StringLength(150)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    public decimal Amount { get; set; }

    public bool IsOptional { get; set; }

    public int SortOrder { get; set; }

    [StringLength(150)]
    public string? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
