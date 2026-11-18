using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ECert.Models;

/// <summary>
///عملية ترحيل رصيد دورة مُحصل إلى الصندوق العام.
/// كل عملية تُسجَّل في سجل ثابت (ledger)؛
/// "الرصيد المرحّل" يُحتسب كـ SUM لكل دورة،
/// و"الرصيد دون ترحيل" = المُحصل − المُرحَّل.
/// </summary>
public class CashboxTransfer
{
    [Key]
    public int Id { get; set; }

    /// <summary>null تعني ترحيل إجمالي خارج سياق دورة بعينها (اختياري).</summary>
    public int? CourseId { get; set; }
    public Course? Course { get; set; }

    [NotMapped]
    public int CourseLedgerId => CourseId ?? 0;

    [Display(Name = "المبلغ")]
    [Range(0.01, double.MaxValue, ErrorMessage = "المبلغ يجب أن يكون أكبر من صفر")]
    public decimal Amount { get; set; }

    [StringLength(500)]
    [Display(Name = "ملاحظة")]
    public string? Note { get; set; }

    [Required]
    [StringLength(150)]
    [Display(Name = "أنشأها")]
    public string CreatedBy { get; set; } = string.Empty;

    [Display(Name = "تاريخ الترحيل")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public static class CashboxRoles
{
    public const string SystemManager = "SuperAdmin";      // مدير النظام — الجهة الوحيدة التي تتعامل مع الصندوق الرئيسي

    // The main fund is reachable only by the SuperAdmin. Collections flow into an employee's
    // custody first, and reach the fund solely through an approved handover (توريد) from
    // /Custody — the single, auditable path. Money leaves the fund only through an approved
    // expense (مصروف معتمد) or a refund paid straight from the fund. There is deliberately no
    // standalone "withdrawal" movement and no per-course "transfer" permission: both legacy
    // paths are retired.
}
