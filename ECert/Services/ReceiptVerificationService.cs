using System.Text;
using System.Text.RegularExpressions;

namespace ECert.Services;

/// <summary>
/// Builds the public verification links and QR images for payment and refund receipts. It reuses the
/// same HMAC scheme as certificates (<see cref="CertificateSecurityService"/>) so a receipt's public
/// link is tamper-evident: the opaque public id alone is not enough, a matching <c>sig</c> is required.
/// </summary>
public sealed class ReceiptVerificationService
{
    private readonly CertificateSecurityService _security;
    private readonly CertificateQrService _qr;

    public ReceiptVerificationService(CertificateSecurityService security, CertificateQrService qr)
    {
        _security = security;
        _qr = qr;
    }

    /// <summary>Public, anonymous verification URL for a receipt's opaque id.</summary>
    public string BuildVerificationUrl(string publicId, string baseUrl)
    {
        var normalizedBase = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        var safeId = Uri.EscapeDataString(publicId.Trim());
        var signature = Uri.EscapeDataString(_security.ComputeSignature(publicId));
        return $"{normalizedBase}/r/{safeId}?sig={signature}";
    }

    public bool VerifySignature(string publicId, string? signature) =>
        _security.VerifySignature(publicId, signature);

    /// <summary>QR PNG (data URI) that encodes the verification URL, for embedding in a receipt.</summary>
    public string BuildQrDataUri(string verificationUrl, int pixelsPerModule = 6) =>
        _qr.BuildDataUri(verificationUrl, pixelsPerModule);
}

/// <summary>
/// Builds a <c>wa.me</c> deep link with a pre-filled Arabic message. Yemeni local numbers
/// (<c>7xxxxxxxx</c> or <c>07xxxxxxxx</c>) are normalised to the international <c>+967</c> form;
/// numbers already carrying a country code are passed through.
/// </summary>
public static class WhatsAppLinkBuilder
{
    private const string DefaultCountryCode = "967";

    public static string NormalizePhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return string.Empty;

        var hasPlus = phone.TrimStart().StartsWith('+');
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return string.Empty;

        if (hasPlus) return "+" + digits;

        // Already includes the country code (967...) without a plus.
        if (digits.StartsWith(DefaultCountryCode) && digits.Length >= 11)
            return "+" + digits;

        // Local format with a leading zero: 07xxxxxxxx -> +967 7xxxxxxxx
        if (digits.StartsWith("0"))
            digits = digits.TrimStart('0');

        // Yemeni mobile numbers start with 7 and are 9 digits long.
        if (digits.Length == 9 && digits.StartsWith('7'))
            return "+" + DefaultCountryCode + digits;

        // Any other length: assume it already carries a country code.
        return "+" + digits;
    }

    /// <summary>Returns a wa.me link, or an empty string when no usable phone number is available.</summary>
    public static string BuildLink(string? phone, string message)
    {
        var normalized = NormalizePhone(phone);
        if (string.IsNullOrEmpty(normalized)) return string.Empty;

        var digitsOnly = new string(normalized.Where(char.IsDigit).ToArray());
        var encoded = Uri.EscapeDataString(message ?? string.Empty);
        return $"https://wa.me/{digitsOnly}?text={encoded}";
    }

    /// <summary>Composes the standard Arabic receipt message sent to a trainee.</summary>
    public static string BuildReceiptMessage(
        string institutionName,
        string receiptKind,
        string receiptNumber,
        string traineeName,
        string courseName,
        decimal amount,
        decimal remaining,
        string verificationUrl)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{institutionName}");
        builder.AppendLine($"{receiptKind} رقم: {receiptNumber}");
        builder.AppendLine($"المتدرب: {traineeName}");
        if (!string.IsNullOrWhiteSpace(courseName))
            builder.AppendLine($"الدورة: {courseName}");
        builder.AppendLine($"المبلغ: {amount:N2} ريال");
        builder.AppendLine($"المتبقي: {remaining:N2} ريال");
        if (!string.IsNullOrWhiteSpace(verificationUrl))
            builder.AppendLine($"للتحقق من صحة السند: {verificationUrl}");
        return builder.ToString().TrimEnd();
    }
}
