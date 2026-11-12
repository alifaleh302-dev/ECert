using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ECert.Models;

/// <summary>
/// An installment plan (خطة أقساط / خطة دراسية) attached to one invoice: the total is split into
/// dated installments. The plan is only a schedule — the money still arrives as ordinary payments —
/// but it lets the system warn about an upcoming due date and report a late installment.
/// </summary>
public class InstallmentPlan
{
    [Key]
    public int InstallmentPlanId { get; set; }

    [Required]
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    /// <summary>Denormalised for reporting so a plan can be grouped by course without a join.</summary>
    [Required]
    public int RegistrationId { get; set; }

    [Display(Name = "إجمالي الخطة")]
    public decimal TotalAmount { get; set; }

    [StringLength(500)]
    [Display(Name = "ملاحظة")]
    public string? Note { get; set; }

    [Required]
    [StringLength(150)]
    [Display(Name = "أنشأها")]
    public string CreatedBy { get; set; } = string.Empty;

    [Display(Name = "تاريخ الإنشاء")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public List<Installment> Installments { get; set; } = new();
}

/// <summary>
/// One dated installment inside a plan. It has no balance of its own: whether it is "paid" is derived
/// by comparing its due amount against the invoice's paid amount in due-date order, so a schedule can
/// never drift from the real ledger.
/// </summary>
public class Installment
{
    [Key]
    public int InstallmentId { get; set; }

    [Required]
    public int InstallmentPlanId { get; set; }
    public InstallmentPlan? Plan { get; set; }

    [Display(Name = "الترتيب")]
    public int Sequence { get; set; }

    [Display(Name = "المبلغ المستحق")]
    [Range(0.01, double.MaxValue, ErrorMessage = "المبلغ يجب أن يكون أكبر من صفر")]
    public decimal Amount { get; set; }

    [Display(Name = "تاريخ الاستحقاق")]
    public DateTime DueDate { get; set; } = DateTime.Now;
}
