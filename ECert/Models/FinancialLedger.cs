using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ECert.Models;

/// <summary>
/// A single signed movement in a financial employee's cash custody (عهدة الموظف). The balance of
/// the custody is always <c>SUM(Amount)</c>, never a stored column, so it can never drift from the
/// underlying collections, refunds and handovers. Positive entries are money the employee collected
/// (or adjustments in their favour); negative entries are refunds they paid out and amounts they
/// handed over to the main fund.
/// </summary>
public class CustodyEntry
{
    [Key]
    public int CustodyEntryId { get; set; }

    [Required]
    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>Signed amount: positive credits the custody, negative debits it.</summary>
    [Display(Name = "المبلغ")]
    public decimal Amount { get; set; }

    [Required]
    [StringLength(40)]
    [Display(Name = "نوع الحركة")]
    public string EntryType { get; set; } = CustodyEntryTypes.Collection;

    [StringLength(40)]
    [Display(Name = "نوع المرجع")]
    public string? ReferenceType { get; set; }

    [Display(Name = "رقم المرجع")]
    public int? ReferenceId { get; set; }

    [StringLength(80)]
    [Display(Name = "رقم السند")]
    public string? ReferenceNumber { get; set; }

    [StringLength(500)]
    [Display(Name = "ملاحظة")]
    public string? Note { get; set; }

    [Required]
    [StringLength(150)]
    [Display(Name = "سجّلها")]
    public string CreatedBy { get; set; } = string.Empty;

    [Display(Name = "التاريخ والوقت")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public static class CustodyEntryTypes
{
    public const string Collection = "Collection";   // مبلغ محصَّل من المتدرب
    public const string Refund = "Refund";           // استرداد رسوم دُفع نقداً من العهدة
    public const string Handover = "Handover";       // توريد/ترحيل إلى الصندوق الرئيسي
    public const string Adjustment = "Adjustment";   // تسوية يدوية (SuperAdmin فقط)
}

/// <summary>
/// A request by a financial employee to hand over part or all of their custody to the main fund
/// (توريد / ترحيل للصندوق الرئيسي). It stays <see cref="CustodyHandoverStatus.Pending"/> until a
/// SuperAdmin approves it; approval is what actually debits the custody and credits the main fund,
/// inside one transaction. A rejected request changes nothing.
/// </summary>
public class CustodyHandover
{
    [Key]
    public int CustodyHandoverId { get; set; }

    [Required]
    public int UserId { get; set; }
    public User? User { get; set; }

    [Display(Name = "المبلغ")]
    [Range(0.01, double.MaxValue, ErrorMessage = "المبلغ يجب أن يكون أكبر من صفر")]
    public decimal Amount { get; set; }

    [Required]
    [StringLength(40)]
    [Display(Name = "الحالة")]
    public string Status { get; set; } = CustodyHandoverStatus.Pending;

    [StringLength(500)]
    [Display(Name = "ملاحظة الموظف")]
    public string? Note { get; set; }

    [Required]
    [StringLength(150)]
    [Display(Name = "قدّمها")]
    public string RequestedBy { get; set; } = string.Empty;

    [Display(Name = "تاريخ التقديم")]
    public DateTime RequestedAt { get; set; } = DateTime.Now;

    [StringLength(150)]
    [Display(Name = "اعتمدها / رفضها")]
    public string? ReviewedBy { get; set; }

    [Display(Name = "تاريخ المراجعة")]
    public DateTime? ReviewedAt { get; set; }

    [StringLength(500)]
    [Display(Name = "ملاحظة المراجعة")]
    public string? ReviewNote { get; set; }
}

public static class CustodyHandoverStatus
{
    public const string Pending = "Pending";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
}

/// <summary>
/// A fee refund (سند صرف / استرداد) tied to an invoice and its registration. Instead of cancelling
/// an old payment — which would corrupt the daily reconciliation — a refund is a new dated movement
/// that reduces the invoice's paid amount and debits the custody of the employee who performed it.
/// A reason is mandatory.
/// </summary>
public class Refund
{
    [Key]
    public int RefundId { get; set; }

    [Required]
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    /// <summary>Denormalised for reporting so a refund can be grouped by registration without a join.</summary>
    [Required]
    public int RegistrationId { get; set; }

    [Display(Name = "المبلغ")]
    [Range(0.01, double.MaxValue, ErrorMessage = "المبلغ يجب أن يكون أكبر من صفر")]
    public decimal Amount { get; set; }

    [Required]
    [StringLength(500)]
    [Display(Name = "سبب الاسترداد")]
    public string Reason { get; set; } = string.Empty;

    [StringLength(40)]
    [Display(Name = "طريقة الاسترداد")]
    public string RefundMethod { get; set; } = "Cash";

    [Display(Name = "رقم السند")]
    [StringLength(80)]
    public string? ReceiptNumber { get; set; }

    [Display(Name = "تاريخ الاسترداد")]
    public DateTime RefundDate { get; set; } = DateTime.Now;

    [Required]
    [StringLength(150)]
    [Display(Name = "نفّذه")]
    public string RefundedBy { get; set; } = string.Empty;

    /// <summary>Opaque id used by the public verification link/QR; never exposes the sequential key.</summary>
    [StringLength(64)]
    public string? PublicId { get; set; }

    [Display(Name = "التاريخ والوقت")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Append-only record of every financial movement: collections, refunds, cancellations,
/// exemptions, custody handovers, transfers and withdrawals. Rows are only ever inserted and read;
/// no code path updates or deletes them, which is what makes the log trustworthy as an audit trail.
/// </summary>
public class FinancialAuditEntry
{
    [Key]
    public int FinancialAuditEntryId { get; set; }

    [Required]
    [StringLength(150)]
    [Display(Name = "المستخدم")]
    public string UserName { get; set; } = string.Empty;

    [Required]
    [StringLength(40)]
    [Display(Name = "نوع العملية")]
    public string ActionType { get; set; } = FinancialActionTypes.Collection;

    [Display(Name = "المبلغ")]
    public decimal Amount { get; set; }

    [StringLength(40)]
    [Display(Name = "نوع المرجع")]
    public string? ReferenceType { get; set; }

    [Display(Name = "رقم المرجع")]
    public int? ReferenceId { get; set; }

    [StringLength(80)]
    [Display(Name = "رقم المستند")]
    public string? ReferenceNumber { get; set; }

    [StringLength(1000)]
    [Display(Name = "التفاصيل")]
    public string? Details { get; set; }

    [Display(Name = "التاريخ والوقت")]
    public DateTime Timestamp { get; set; } = DateTime.Now;
}

public static class FinancialActionTypes
{
    public const string Collection = "Collection";   // قبض
    public const string Refund = "Refund";           // استرداد
    public const string Cancel = "Cancel";           // إلغاء
    public const string Exemption = "Exemption";     // إعفاء / خصم
    public const string Handover = "Handover";       // ترحيل عهدة
    public const string Transfer = "Transfer";       // ترحيل رصيد دورة للصندوق
    public const string Withdraw = "Withdraw";       // سحب من الصندوق
}
