using QRCoder;

namespace ECert.Services;

/// <summary>
/// Renders a certificate's public verification URL as a QR image. The PNG writer is used because
/// it has no native (System.Drawing) dependency, so it works the same on the Linux container as it
/// does in development.
/// </summary>
public sealed class CertificateQrService
{
    /// <summary>Returns the QR code for <paramref name="content"/> as raw PNG bytes.</summary>
    public byte[] BuildPng(string content, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content ?? string.Empty, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return png.GetGraphic(Math.Clamp(pixelsPerModule, 2, 20));
    }

    /// <summary>Returns a <c>data:image/png;base64,...</c> URI ready to drop into an <c>img src</c>.</summary>
    public string BuildDataUri(string content, int pixelsPerModule = 8)
        => "data:image/png;base64," + Convert.ToBase64String(BuildPng(content, pixelsPerModule));

    /// <summary>Returns a base64 payload (without the data-URI prefix), e.g. for embedding in exports.</summary>
    public string BuildBase64(string content, int pixelsPerModule = 8)
        => Convert.ToBase64String(BuildPng(content, pixelsPerModule));

    /// <summary>
    /// Returns the QR module matrix as a square grid of <c>true</c> (dark) / <c>false</c> (light)
    /// cells, including the quiet zone. Used to draw the QR as crisp vector squares in the PDF
    /// export instead of a scaled bitmap.
    /// </summary>
    public bool[,] BuildMatrix(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content ?? string.Empty, QRCodeGenerator.ECCLevel.Q);
        var rows = data.ModuleMatrix;
        var size = rows.Count;
        var matrix = new bool[size, size];
        for (var y = 0; y < size; y++)
        {
            var row = rows[y];
            for (var x = 0; x < size; x++)
                matrix[y, x] = row[x];
        }
        return matrix;
    }
}
