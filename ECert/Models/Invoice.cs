using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

public class Invoice
{
    [Key]
    public int InvoiceId { get; set; }

    [Display(Name = "رقم الفاتورة")]
    public string InvoiceNumber { get; set; } = string.Empty;

    [Required]
    public int RegistrationId { get; set; }
    public Registration? Registration { get; set; }

    [Display(Name = "اسم المتدرب")]
    public string TraineeName { get; set; } = string.Empty;

    [StringLength(150)]
    [Display(Name = "اسم المتدرب بالعربية")]
    public string? TraineeNameArabic { get; set; }

    [StringLength(150)]
    [Display(Name = "اسم المتدرب بالإنجليزية")]
    public string? TraineeNameEnglish { get; set; }

    [Display(Name = "رقم الهاتف")]
    public string TraineePhone { get; set; } = string.Empty;

    [Display(Name = "الدورة")]
    public string CourseName { get; set; } = string.Empty;

    [StringLength(200)]
    [Display(Name = "الدورة بالإنجليزية")]
    public string? CourseNameEnglish { get; set; }

    [StringLength(200)]
    [Display(Name = "الدورة بالعربية")]
    public string? CourseNameArabic { get; set; }

    [Display(Name = "المبلغ الأصلي")]
    public decimal OriginalAmount { get; set; }

    [Display(Name = "مبلغ الإعفاء")]
    public decimal ExemptionAmount { get; set; }

    [Display(Name = "المبلغ الإجمالي المستحق")]
    public decimal TotalAmount { get; set; }

    [Display(Name = "سبب الإعفاء")]
    [StringLength(500)]
    public string? ExemptionReason { get; set; }

    [Display(Name = "المبلغ المدفوع")]
    public decimal PaidAmount { get; set; } = 0;

    [Display(Name = "المبلغ المتبقي")]
    public decimal RemainingAmount => TotalAmount - PaidAmount;

    [Display(Name = "حالة الفاتورة")]
    [StringLength(40)]
    public string Status { get; set; } = "Unpaid";
    // Unpaid, PartiallyPaid, Paid, Cancelled

    [Display(Name = "تاريخ الإنشاء")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Display(Name = "تاريخ الاستحقاق")]
    [DataType(DataType.Date)]
    public DateTime? DueDate { get; set; }

    [Display(Name = "أنشأها")]
    public string? CreatedBy { get; set; }

    [Display(Name = "ألغاها")]
    public string? CancelledBy { get; set; }

    [Display(Name = "تاريخ الإلغاء")]
    public DateTime? CancelledAt { get; set; }

    [Display(Name = "استعادها")]
    public string? RestoredBy { get; set; }

    [Display(Name = "تاريخ الاستعادة")]
    public DateTime? RestoredAt { get; set; }

    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}

public class Payment
{
    [Key]
    public int PaymentId { get; set; }

    [Required]
    public int InvoiceId { get; set; }
    public Invoice? Invoice { get; set; }

    [Display(Name = "المبلغ")]
    [Range(0.01, double.MaxValue, ErrorMessage = "المبلغ يجب أن يكون أكبر من صفر")]
    public decimal Amount { get; set; }

    [Display(Name = "طريقة الدفع")]
    public string PaymentMethod { get; set; } = "Cash";
    // Cash, EWallet (محافظ إلكترونية)

    [Display(Name = "رقم السند")]
    public string? ReferenceNumber { get; set; }

    [Display(Name = "تاريخ الدفع")]
    public DateTime PaymentDate { get; set; } = DateTime.Now;

    [Display(Name = "ملاحظات")]
    public string? Notes { get; set; }

    [Display(Name = "سجلها")]
    public string? RecordedBy { get; set; }

    [Display(Name = "ملغاة")]
    public bool IsCancelled { get; set; } = false;

    [Display(Name = "سبب الإلغاء")]
    public string? CancellationReason { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
