using System.Text;

namespace ECert.Services;

/// <summary>
/// Renders a linear (1D) Code 128 barcode as an SVG image. Code 128 is used because it encodes the
/// full ASCII range, so certificate numbers keep their letters, digits and dashes. SVG is emitted
/// rather than a raster so the barcode stays crisp at any certificate size and needs no image
/// library (the container has no System.Drawing).
/// </summary>
public sealed class CertificateBarcodeService
{
    /// <summary>Bar/space widths for code values 0..106 (start codes, data, checksum, stop).</summary>
    private static readonly string[] Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112"
    };

    private const int StartB = 104;
    private const int Stop = 106;

    /// <summary>Returns an SVG barcode as raw UTF-8 bytes, or an empty array when there is nothing to encode.</summary>
    public byte[] BuildSvg(string content, int moduleWidth = 2, int height = 60)
    {
        var svg = BuildSvgString(content, moduleWidth, height);
        return svg.Length == 0 ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(svg);
    }

    /// <summary>Returns a <c>data:image/svg+xml;base64,...</c> URI ready to drop into an <c>img src</c>.</summary>
    public string BuildDataUri(string content, int moduleWidth = 2, int height = 60)
    {
        var svg = BuildSvgString(content, moduleWidth, height);
        return svg.Length == 0
            ? string.Empty
            : "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    private static string BuildSvgString(string content, int moduleWidth, int height)
    {
        var value = Sanitize(content);
        if (value.Length == 0) return string.Empty;

        moduleWidth = Math.Clamp(moduleWidth, 1, 6);
        height = Math.Clamp(height, 20, 200);

        var codes = new List<int> { StartB };
        foreach (var ch in value) codes.Add(ch - 32);

        // Code 128 checksum: start value plus each data value weighted by its 1-based position.
        var checksum = StartB;
        for (var i = 1; i < codes.Count; i++) checksum += codes[i] * i;
        codes.Add(checksum % 103);
        codes.Add(Stop);

        var width = 0;
        var builder = new StringBuilder();
        foreach (var code in codes)
        {
            var pattern = Patterns[code];
            var isBar = true;
            foreach (var digit in pattern)
            {
                var modules = digit - '0';
                var span = modules * moduleWidth;
                if (isBar)
                    builder.Append($"<rect x=\"{width}\" y=\"0\" width=\"{span}\" height=\"{height}\"/>");
                width += span;
                isBar = !isBar;
            }
        }

        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {width} {height}\" " +
               $"width=\"{width}\" height=\"{height}\" preserveAspectRatio=\"none\" role=\"img\">" +
               $"<rect x=\"0\" y=\"0\" width=\"{width}\" height=\"{height}\" fill=\"#ffffff\"/>" +
               $"<g fill=\"#000000\">{builder}</g></svg>";
    }

    /// <summary>Keeps only characters Code 128 set B can encode (ASCII 32..126).</summary>
    private static string Sanitize(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return string.Empty;
        var builder = new StringBuilder(content.Length);
        foreach (var ch in content)
        {
            if (ch >= 32 && ch <= 126) builder.Append(ch);
        }
        return builder.ToString().Trim();
    }
}
