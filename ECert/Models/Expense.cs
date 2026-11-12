using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ECert.Models;

/// <summary>
/// A category an expense belongs to (بنود المصروفات: إيجار، رواتب، ...). Kept as data so an
/// institution can shape its own chart of expenses without a code change.
/// </summary>
public class ExpenseCategory
{
    [Key]
    public int ExpenseCategoryId { get; set; }

    [Required]
    [StringLength(120)]
    [Display(Name = "اسم البند")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "نشط")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "الترتيب")]
    public int SortOrder { get; set; }

    [Display(Name = "تاريخ الإنشاء")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// A single expense (مصروف) with an optional attachment (invoice/receipt scan). An expense is a new
/// dated movement: it is approved by a SuperAdmin before it counts, and it is never edited once
/// approved — a correction is a new expense, so the audit trail stays append-only.
/// </summary>
public class Expense
{
    [Key]
    public int ExpenseId { get; set; }

    [Required]
    [Display(Name = "بند المصروف")]
    public int ExpenseCategoryId { get; set; }
    public ExpenseCategory? Category { get; set; }

    [Required]
    [StringLength(300)]
    [Display(Name = "البيان")]
    public string Description { get; set; } = string.Empty;

    [Display(Name = "المبلغ")]
    [Range(0.01, double.MaxValue, ErrorMessage = "المبلغ يجب أن يكون أكبر من صفر")]
    public decimal Amount { get; set; }

    [Display(Name = "تاريخ المصروف")]
    public DateTime ExpenseDate { get; set; } = DateTime.Now;

    [StringLength(80)]
    [Display(Name = "رقم المستند")]
    public string? DocumentNumber { get; set; }

    /// <summary>Optional uploaded invoice/receipt image, stored under wwwroot/uploads/expenses.</summary>
    [StringLength(400)]
    [Display(Name = "المرفق")]
    public string? AttachmentUrl { get; set; }

    [Required]
    [StringLength(40)]
    [Display(Name = "الحالة")]
    public string Status { get; set; } = ExpenseStatus.PendingApproval;

    [Required]
    [StringLength(150)]
    [Display(Name = "قدّمه")]
    public string SubmittedBy { get; set; } = string.Empty;

    [Display(Name = "تاريخ التقديم")]
    public DateTime SubmittedAt { get; set; } = DateTime.Now;

    [StringLength(150)]
    [Display(Name = "اعتمده / رفضه")]
    public string? ReviewedBy { get; set; }

    [Display(Name = "تاريخ المراجعة")]
    public DateTime? ReviewedAt { get; set; }

    [StringLength(500)]
    [Display(Name = "ملاحظة المراجعة")]
    public string? ReviewNote { get; set; }
}

public static class ExpenseStatus
{
    public const string PendingApproval = "PendingApproval"; // بانتظار الاعتماد
    public const string Approved = "Approved";               // معتمد — يُحتسب في التقارير
    public const string Rejected = "Rejected";               // مرفوض
}
