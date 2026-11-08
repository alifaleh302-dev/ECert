using ECert.Services;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Guards the URL scheme allow-list used before admin-editable values reach href/src/url().
/// Razor escapes quotes but not the scheme, so these cases are the difference between a
/// stored javascript: payload executing or being neutralised.
/// </summary>
public class SafeUrlTests
{
    [Theory]
    [InlineData("https://example.com/page")]
    [InlineData("http://example.com")]
    [InlineData("/Courses")]
    [InlineData("//cdn.example.com/x.png")]
    [InlineData("#section")]
    [InlineData("?q=1")]
    [InlineData("images/logo.png")]
    [InlineData("mailto:info@example.com")]
    [InlineData("tel:+967123456")]
    [InlineData("HTTPS://Example.com")]
    public void Allows_safe_targets(string url)
    {
        Assert.Equal(url, SafeUrl.ForAttribute(url, "#"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("  javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("java\nscript:alert(1)")]
    [InlineData("java script:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///etc/passwd")]
    public void Rejects_dangerous_schemes_and_falls_back(string url)
    {
        Assert.Equal("#", SafeUrl.ForAttribute(url, "#"));
    }

    [Fact]
    public void Empty_input_returns_the_fallback()
    {
        Assert.Null(SafeUrl.ForAttribute(null));
        Assert.Equal("#", SafeUrl.ForAttribute("   ", "#"));
    }

    [Fact]
    public void Css_url_neutralises_declaration_breakouts()
    {
        var value = SafeUrl.CssUrl("https://x/a.png');background:url('evil");

        Assert.DoesNotContain("'", value);
        Assert.DoesNotContain("(", value);
        Assert.DoesNotContain(")", value);
        Assert.DoesNotContain(";", value);
    }

    [Fact]
    public void Css_url_rejects_disallowed_scheme_entirely()
    {
        Assert.Equal(string.Empty, SafeUrl.CssUrl("javascript:alert(1)"));
    }
}
