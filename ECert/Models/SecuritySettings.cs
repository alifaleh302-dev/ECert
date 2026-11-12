using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

/// <summary>
/// The single row (Id = 1) of operator-editable security configuration. Every value is read at
/// runtime by the security services, so an administrator can change the login lockout policy, the
/// Telegram channels or the backup encryption key from the <c>/SecuritySettings</c> page without a
/// redeploy. The row is seeded with safe defaults on first boot.
/// </summary>
public class SecuritySettings
{
    public const int SingletonId = 1;

    [Key]
    public int SecuritySettingsId { get; set; } = SingletonId;

    /// <summary>Bot token used for 2FA codes, account linking and channel notifications.</summary>
    [Display(Name = "توكن بوت تليجرام")]
    public string? TelegramBotToken { get; set; }

    /// <summary>Chat id of the channel that receives real-time security alerts.</summary>
    [Display(Name = "قناة التنبيهات الأمنية")]
    public string? SecurityAlertChannelId { get; set; }

    /// <summary>Chat id of the archive channel that receives encrypted backups.</summary>
    [Display(Name = "قناة النسخ الاحتياطي")]
    public string? BackupChannelId { get; set; }

    /// <summary>Passphrase used to derive the AES-256 key that encrypts every backup.</summary>
    [Display(Name = "مفتاح تشفير النسخ الاحتياطي")]
    public string? BackupEncryptionKey { get; set; }

    /// <summary>When true, a Telegram OTP is required after the password check.</summary>
    [Display(Name = "تفعيل المصادقة الثنائية")]
    public bool TwoFactorEnabled { get; set; }

    /// <summary>Failed attempts (per account or per address) before the identity is locked out.</summary>
    [Display(Name = "الحد الأقصى لمحاولات الدخول الفاشلة")]
    public int MaxFailedLoginAttempts { get; set; } = 5;

    /// <summary>How long a locked-out account or blocked address stays blocked.</summary>
    [Display(Name = "مدة الحظر بالدقائق")]
    public int LockoutMinutes { get; set; } = 15;

    /// <summary>Signed-in session is ended after this many minutes without a request.</summary>
    [Display(Name = "مهلة خمول الجلسة بالدقائق")]
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>Whether the daily encrypted backup job runs.</summary>
    [Display(Name = "تفعيل النسخ الاحتياطي اليومي")]
    public bool BackupEnabled { get; set; } = true;

    /// <summary>Hour (UTC) of the daily backup.</summary>
    [Display(Name = "ساعة النسخ الاحتياطي (UTC)")]
    public int BackupHourUtc { get; set; } = 2;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [StringLength(150)]
    public string? UpdatedBy { get; set; }
}
