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

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}
