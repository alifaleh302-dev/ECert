using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

public class AppNotification
{
    [Key]
    public int NotificationId { get; set; }

    [Display(Name = "المستلم")]
    public string Recipient { get; set; } = string.Empty;

    [Display(Name = "رقم الهاتف")]
    public string? PhoneNumber { get; set; }

    [Display(Name = "البريد الإلكتروني")]
    public string? Email { get; set; }

    [Display(Name = "نوع الإشعار")]
    public string NotificationType { get; set; } = string.Empty;
    // RegistrationAccepted, RegistrationRejected, PaymentReminder, CertificateIssued

    [Display(Name = "الرسالة")]
    public string Message { get; set; } = string.Empty;

    [Display(Name = "القناة")]
    public string Channel { get; set; } = "SMS";
    // SMS, WhatsApp, Email

    [Display(Name = "الحالة")]
    [StringLength(40)]
    public string Status { get; set; } = "Pending";
    // Pending, Sent, Failed

    [Display(Name = "وقت الإرسال")]
    public DateTime? SentAt { get; set; }

    [Display(Name = "عدد المحاولات")]
    public int RetryCount { get; set; } = 0;

    [Display(Name = "رسالة الخطأ")]
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [Display(Name = "العنصر المرتبط")]
    public string? RelatedEntityType { get; set; }

    public int? RelatedEntityId { get; set; }
}
