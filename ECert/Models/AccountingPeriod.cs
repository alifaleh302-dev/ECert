using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

/// <summary>
/// A financial period lock: once a period is closed up to <see cref="LockedThrough"/>, no payment,
/// refund, handover or cashbox movement dated on or before that day may be added, cancelled or
/// edited. Locks are kept as an append-only history — closing a period inserts a new active row and
/// deactivates the previous one, and reopening simply deactivates the current lock — so who locked
/// or reopened a period, and when, is always recoverable.
/// </summary>
public class AccountingPeriodLock
{
    [Key]
    public int AccountingPeriodLockId { get; set; }

    /// <summary>The last day (inclusive) that is closed to financial edits.</summary>
    [Display(Name = "مقفلة حتى تاريخ")]
    public DateTime LockedThrough { get; set; }

    [StringLength(500)]
    [Display(Name = "ملاحظة")]
    public string? Note { get; set; }

    /// <summary>True for the lock currently in force; false once superseded or reopened.</summary>
    public bool IsActive { get; set; } = true;

    [Required]
    [StringLength(150)]
    [Display(Name = "أقفلها")]
    public string LockedBy { get; set; } = string.Empty;

    [Display(Name = "تاريخ الإقفال")]
    public DateTime LockedAt { get; set; } = DateTime.Now;
}
