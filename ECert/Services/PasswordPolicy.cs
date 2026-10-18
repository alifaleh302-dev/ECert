using System.ComponentModel.DataAnnotations;

namespace ECert.Services;

/// <summary>
/// Shared password rules, applied both when creating a user and when resetting a password.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;

    private static readonly string[] CommonPasswords =
    {
        "password", "passw0rd", "12345678", "123456789", "qwerty123", "admin123",
        "admin1234", "letmein123", "welcome123", "iloveyou", "123123123", "password1"
    };

    public static IEnumerable<ValidationResult> Validate(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            yield return new ValidationResult("كلمة المرور مطلوبة.", new[] { "Password" });
            yield break;
        }

        if (password.Length < MinimumLength)
            yield return new ValidationResult($"كلمة المرور يجب أن تكون {MinimumLength} أحرف على الأقل.", new[] { "Password" });

        if (password.Length > 128)
            yield return new ValidationResult("كلمة المرور طويلة جداً.", new[] { "Password" });

        var hasLetter = password.Any(char.IsLetter);
        var hasDigit = password.Any(char.IsDigit);

        if (!hasLetter || !hasDigit)
            yield return new ValidationResult("كلمة المرور يجب أن تحتوي على حرف ورقم على الأقل.", new[] { "Password" });

        if (CommonPasswords.Contains(password.ToLowerInvariant()))
            yield return new ValidationResult("كلمة المرور شائعة جداً، اختر كلمة مرور أقوى.", new[] { "Password" });
    }
}

/// <summary>
/// Applied to <c>CreateUserViewModel</c>; see also <see cref="PasswordPolicy"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class StrongPasswordAttribute : ValidationAttribute
{
    public StrongPasswordAttribute() => ErrorMessage = "كلمة المرور لا تستوفي الشروط.";

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        var password = value as string;
        var result = PasswordPolicy.Validate(password).FirstOrDefault();
        return result ?? ValidationResult.Success;
    }
}
