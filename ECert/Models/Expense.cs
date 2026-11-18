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

    /// <summary>
    /// سقف الميزانية المسموح صرفه تحت هذا البند. <c>null</c> يعني بلا حد أقصى.
    /// يُرفض أي مصروف يجعل مجموع المصروفات غير المرفوضة تحت البند يتجاوز هذا السقف.
    /// </summary>
    [Display(Name = "الحد الأقصى للميزانية")]
    [Range(0.01, double.MaxValue, ErrorMessage = "الحد الأقصى يجب أن يكون أكبر من صفر")]
    public decimal? MaxAmount { get; set; }

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

    /// <summary>
    /// Legacy single attachment. New submissions store their images in <see cref="Attachments"/>;
    /// this column is kept so expenses recorded before multi-image support still render.
    /// </summary>
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

    /// <summary>
    /// The id of the user who submitted the expense. It lets the submitter withdraw their own
    /// request while it is still pending, without relying on the display name (which is not unique).
    /// Null for expenses recorded before this column existed.
    /// </summary>
    [Display(Name = "معرّف مقدّم الطلب")]
    public int? SubmittedByUserId { get; set; }

    [Display(Name = "تاريخ التقديم")]
    public DateTime SubmittedAt { get; set; } = DateTime.Now;

    /// <summary>Images attached to the expense (invoices/receipts), at most five.</summary>
    public ICollection<ExpenseAttachment> Attachments { get; set; } = new List<ExpenseAttachment>();

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
    public const string Cancelled = "Cancelled";             // ملغى من مقدّمه قبل الاعتماد
}

/// <summary>
/// One image attached to an expense (an invoice/receipt scan or a photo taken with the camera).
/// Stored under wwwroot/uploads/expenses. An expense carries at most
/// <see cref="ExpenseAttachment.MaxPerExpense"/> of them.
/// </summary>
public class ExpenseAttachment
{
    public const int MaxPerExpense = 5;

    [Key]
    public int ExpenseAttachmentId { get; set; }

    [Display(Name = "المصروف")]
    public int ExpenseId { get; set; }
    public Expense? Expense { get; set; }

    [Required]
    [StringLength(400)]
    [Display(Name = "رابط الصورة")]
    public string Url { get; set; } = string.Empty;

    [Display(Name = "الترتيب")]
    public int SortOrder { get; set; }
}
