using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

public class User
{
    [Key]
    public int UserId { get; set; }

    [Required(ErrorMessage = "اسم المستخدم مطلوب")]
    [StringLength(50)]
    [Display(Name = "اسم المستخدم")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "الاسم الكامل مطلوب")]
    [StringLength(100)]
    [Display(Name = "الاسم الكامل")]
    public string FullName { get; set; } = string.Empty;

    [Required(ErrorMessage = "البريد الإلكتروني مطلوب")]
    [EmailAddress(ErrorMessage = "البريد الإلكتروني غير صحيح")]
    [Display(Name = "البريد الإلكتروني")]
    public string Email { get; set; } = string.Empty;

    [Display(Name = "رقم الهاتف")]
    public string? Phone { get; set; }

    public string PasswordHash { get; set; } = string.Empty;

    [Display(Name = "نشط")]
    public bool IsActive { get; set; } = true;

    [Display(Name = "تاريخ الإنشاء")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? LastLoginAt { get; set; }

    /// <summary>
    /// Set for accounts created by the seeder with a well-known bootstrap password.
    /// The user is forced to choose a new password before using the application.
    /// </summary>
    public bool MustChangePassword { get; set; }

    /// <summary>
    /// Telegram chat id bound to this account. When set (and 2FA is enabled globally), a login
    /// must be confirmed with an OTP delivered to this chat.
    /// </summary>
    [StringLength(40)]
    [Display(Name = "معرّف تليجرام")]
    public string? TelegramChatId { get; set; }

    /// <summary>
    /// One-time code the employee sends to the bot to bind their chat. Null once linked.
    /// </summary>
    [StringLength(20)]
    public string? TelegramLinkCode { get; set; }

    public DateTime? TelegramLinkedAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();

    /// <summary>
    /// Courses this user may administer. Empty means unrestricted (every course), which is the
    /// case for SuperAdmin and for roles that are not course-scoped.
    /// </summary>
    public ICollection<UserCourse> CourseAssignments { get; set; } = new List<UserCourse>();

    /// <summary>
    /// Categories whose courses this user may administer, including courses added later. Combined
    /// with <see cref="CourseAssignments"/> to form the effective scope; empty on both means
    /// unrestricted.
    /// </summary>
    public ICollection<UserCategory> CategoryAssignments { get; set; } = new List<UserCategory>();
}
