namespace ECert.Services;

/// <summary>
/// Validates user-uploaded images before they are written to disk.
/// </summary>
public static class ImageUploadValidator
{
    public const long MaxBytes = 5 * 1024 * 1024;

    private static readonly Dictionary<string, byte[][]> SignaturesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".jpeg"] = new[] { new byte[] { 0xFF, 0xD8, 0xFF } },
        [".png"] = new[] { new byte[] { 0x89, 0x50, 0x4E, 0x47 } },
        [".gif"] = new[] { new byte[] { 0x47, 0x49, 0x46, 0x38 } },
        [".webp"] = new[] { new byte[] { 0x52, 0x49, 0x46, 0x46 } },
        [".ico"] = new[] { new byte[] { 0x00, 0x00, 0x01, 0x00 } }
    };

    public static IReadOnlyCollection<string> AllowedExtensions => SignaturesByExtension.Keys;

    /// <summary>
    /// Checks size, extension, and magic bytes. The signature check matters because the
    /// extension alone is caller-controlled: it blocks SVG and HTML payloads that would
    /// otherwise be served from our own origin.
    /// </summary>
    public static async Task<(bool Ok, string? Error)> ValidateAsync(IFormFile file, CancellationToken cancellationToken = default)
    {
        if (file.Length == 0)
            return (false, "الملف فارغ.");

        if (file.Length > MaxBytes)
            return (false, $"حجم الصورة يجب ألا يتجاوز {MaxBytes / (1024 * 1024)} ميغابايت.");

        var extension = Path.GetExtension(file.FileName);
        if (!SignaturesByExtension.TryGetValue(extension, out var signatures))
            return (false, $"صيغة الصورة غير مدعومة. الصيغ المتاحة: {string.Join(", ", AllowedExtensions)}");

        var header = new byte[8];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAsync(header, cancellationToken);
        if (read < 4 || !signatures.Any(signature => header.AsSpan(0, signature.Length).SequenceEqual(signature)))
            return (false, "محتوى الملف لا يطابق صيغة صورة صحيحة.");

        return (true, null);
    }
}
