using System.Globalization;
using System.Text;

namespace ECert.Services.Excel;

/// <summary>
/// Maps the header text of an uploaded sheet onto the canonical column keys used by the import
/// parsers. Matching folds case, whitespace, Arabic letter variants and the decorative marks
/// Arabic keyboards emit, so a header written as "الاسم بالعربية" matches "الاسمالعربي".
/// </summary>
public static class ExcelHeaderMap
{
    public static string? Resolve(string? rawHeader, IReadOnlyDictionary<string, string[]> aliases)
    {
        var normalized = Normalize(rawHeader);
        if (normalized.Length == 0) return null;

        foreach (var (key, candidates) in aliases)
            foreach (var candidate in candidates)
                if (Normalize(candidate) == normalized)
                    return key;

        return null;
    }

    /// <summary>Folds a header to a comparable form: no case, no spaces, no punctuation, unified Arabic letters.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            // Drop combining marks (Arabic tashkeel, Latin accents).
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(ch) || ch is '_' or '-' or '.' or '(' or ')' or '[' or ']' or '/' or '\\' or ':') continue;
            builder.Append(ch);
        }

        var text = builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();

        return text
            .Replace('أ', 'ا')
            .Replace('إ', 'ا')
            .Replace('آ', 'ا')
            .Replace('ى', 'ي')
            .Replace('ة', 'ه')
            .Replace('ؤ', 'و')
            .Replace('ئ', 'ي');
    }
}
