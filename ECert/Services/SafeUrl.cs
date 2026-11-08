using System.Text;

namespace ECert.Services;

/// <summary>
/// Guards URLs that come from admin-editable fields before they are written into
/// <c>href</c>/<c>src</c>/<c>url()</c>. Razor's HTML encoder escapes quotes and angle
/// brackets but leaves the scheme intact, so a stored <c>javascript:</c> value would
/// otherwise execute for every visitor. Only http(s), mailto, tel and app-relative
/// targets are allowed; anything else (including <c>javascript:</c>, <c>data:</c>,
/// <c>vbscript:</c>) collapses to the supplied fallback.
/// </summary>
public static class SafeUrl
{
    private static readonly HashSet<string> AllowedSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http", "https", "mailto", "tel"
    };

    /// <summary>
    /// Returns a value safe to place in a URL attribute, or <paramref name="fallback"/>
    /// when the input is empty, malformed, or uses a disallowed scheme.
    /// </summary>
    public static string? ForAttribute(string? url, string? fallback = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return fallback;

        var value = url.Trim();

        // Same-origin forms: protocol-relative, root-relative, fragment, query.
        if (value.StartsWith("//", StringComparison.Ordinal) ||
            value.StartsWith('/') || value.StartsWith('#') || value.StartsWith('?'))
            return value;

        var colon = value.IndexOf(':');
        if (colon < 0) return value; // no scheme => relative target

        var scheme = value[..colon];

        // Browsers strip whitespace and control characters before parsing the scheme, so
        // "java\tscript:" still resolves to javascript:. Reject any smuggling attempt.
        if (scheme.Length == 0 || scheme.Any(ch => char.IsWhiteSpace(ch) || char.IsControl(ch)))
            return fallback;

        // A real scheme starts with a letter and contains only scheme characters. If the
        // prefix holds anything else (e.g. "images/x.png") it is a relative path, not a scheme.
        if (!char.IsAsciiLetter(scheme[0]) ||
            !scheme.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '+' or '-' or '.'))
            return value;

        return AllowedSchemes.Contains(scheme) ? value : fallback;
    }

    /// <summary>
    /// Same guard as <see cref="ForAttribute"/> but for a value placed inside a CSS
    /// <c>url()</c>. HTML entity decoding happens before CSS parsing, so escaping the
    /// attribute is not enough here: parentheses, quotes, backslashes and semicolons must
    /// be neutralised so the value cannot terminate the <c>url()</c> and start a new
    /// declaration.
    /// </summary>
    public static string CssUrl(string? url)
    {
        var value = ForAttribute(url, string.Empty) ?? string.Empty;
        if (value.Length == 0) return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(ch switch
            {
                '(' => "%28",
                ')' => "%29",
                '"' => "%22",
                '\'' => "%27",
                '\\' => "%5C",
                ';' => "%3B",
                _ => ch.ToString()
            });
        }
        return builder.ToString();
    }
}
