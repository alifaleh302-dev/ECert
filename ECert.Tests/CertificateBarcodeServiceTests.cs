using System.Text;
using System.Text.RegularExpressions;
using ECert.Services;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Guards the linear Code 128 barcode generator. The expected module strings come from the
/// reference implementation (python-barcode), so a regression in the pattern table, the checksum,
/// or the bar/space alternation is caught without needing an image decoder.
/// </summary>
public class CertificateBarcodeServiceTests
{
    private readonly CertificateBarcodeService _service = new();

    [Theory]
    [InlineData("ABC123xyz",
        "11010010000101000110001000101100010001000110100111001101100111001011001011100111100100101101101111011011110110101101110001100011101011")]
    [InlineData("CERT-2020-1",
        "110100100001000100011010001101000110001011101101110001010011011100110011100101001110110011001110010100111011001001101110010011100110110010111001100011101011")]
    public void Bars_and_spaces_match_the_reference_code128(string value, string expectedModules)
    {
        var svg = Encoding.UTF8.GetString(_service.BuildSvg(value, moduleWidth: 1, height: 50));

        Assert.Equal(expectedModules, ExtractModules(svg));
    }

    [Fact]
    public void Svg_has_the_expected_dimensions_and_colours()
    {
        var svg = Encoding.UTF8.GetString(_service.BuildSvg("ABC123xyz", moduleWidth: 1, height: 60));

        Assert.StartsWith("<svg", svg);
        Assert.Contains("viewBox=\"0 0 134 60\"", svg);   // 134 modules at 1 px per module
        Assert.Contains("<g fill=\"#000000\">", svg);
        Assert.Contains("<rect", svg);
    }

    [Fact]
    public void Empty_or_whitespace_content_produces_no_image()
    {
        Assert.Empty(_service.BuildSvg(""));
        Assert.Empty(_service.BuildSvg("   "));
        Assert.Equal(string.Empty, _service.BuildDataUri(""));
    }

    [Fact]
    public void Non_ascii_characters_are_dropped_before_encoding()
    {
        // Arabic letters are outside Code 128 set B, so only the ASCII part is encoded.
        var svg = Encoding.UTF8.GetString(_service.BuildSvg("شهادة-123"));

        Assert.Contains("<rect", svg);
        Assert.DoesNotContain("ش", svg);
    }

    [Fact]
    public void Data_uri_wraps_the_svg_in_base64()
    {
        var uri = _service.BuildDataUri("EC-2026-0042", moduleWidth: 1, height: 40);
        Assert.StartsWith("data:image/svg+xml;base64,", uri);

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(uri["data:image/svg+xml;base64,".Length..]));
        Assert.Contains("<svg", decoded);
        Assert.Contains("<rect", decoded);
    }

    /// <summary>Rebuilds the bar/space module string from the SVG bar rectangles.</summary>
    private static string ExtractModules(string svg)
    {
        var modules = new StringBuilder();
        var cursor = 0;
        foreach (Match rect in Regex.Matches(svg, "<rect x=\"(\\d+)\" y=\"0\" width=\"(\\d+)\" height=\"50\"/>"))
        {
            var x = int.Parse(rect.Groups[1].Value);
            var width = int.Parse(rect.Groups[2].Value);
            modules.Append('0', x - cursor);
            modules.Append('1', width);
            cursor = x + width;
        }
        return modules.ToString();
    }
}
