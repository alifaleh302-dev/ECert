using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

public class AuditLog
{
    [Key]
    public int AuditLogId { get; set; }

    [Display(Name = "المستخدم")]
    public string UserName { get; set; } = string.Empty;

    [Display(Name = "العملية")]
    public string Action { get; set; } = string.Empty;
    // Create, Update, Delete, Accept, Reject, Issue, Cancel, Login

    [Display(Name = "نوع العنصر")]
    public string EntityType { get; set; } = string.Empty;
    // Registration, Invoice, Payment, Certificate, Course, User, Post

    [Display(Name = "رقم العنصر")]
    public int? EntityId { get; set; }

    [Display(Name = "البيانات القديمة")]
    public string? OldValues { get; set; }

    [Display(Name = "البيانات الجديدة")]
    public string? NewValues { get; set; }

    [Display(Name = "التاريخ والوقت")]
    public DateTime Timestamp { get; set; } = DateTime.Now;

    [Display(Name = "عنوان IP")]
    public string? IpAddress { get; set; }

    /// <summary>
    /// Hash of the previous row in the chain (empty for the first row). Together with
    /// <see cref="RowHash"/> this makes the log tamper-evident: altering or deleting any row
    /// breaks every hash that follows it, which the integrity check detects.
    /// </summary>
    [StringLength(64)]
    public string? PreviousHash { get; set; }

    /// <summary>SHA-256 over this row's content plus <see cref="PreviousHash"/>.</summary>
    [StringLength(64)]
    public string? RowHash { get; set; }
}
