using ECert.Models;
using ECert.Models.ViewModels;
using SkiaSharp;

namespace ECert.Services;

/// <summary>
/// Renders certificates as a single, print-ready PDF using SkiaSharp's vector PDF backend. Each
/// certificate becomes one page; the page carries real text (searchable, crisp at any size) and the
/// QR/barcode are drawn as vectors rather than bitmaps. The artwork comes from
/// <see cref="CertificateRenderService"/>, so the PDF matches the on-screen certificate exactly.
/// </summary>
public sealed class CertificatePdfService
{
    /// <summary>A4 in points (1/72 inch): 595.28 × 841.89.</summary>
    private const float A4Short = 595.28f;
    private const float A4Long = 841.89f;

    private readonly CertificateRenderService _render;
    private readonly CertificateQrService _qr;
    private readonly CertificateBarcodeService _barcode;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CertificatePdfService> _logger;

    public CertificatePdfService(
        CertificateRenderService render,
        CertificateQrService qr,
        CertificateBarcodeService barcode,
        IWebHostEnvironment env,
        ILogger<CertificatePdfService> logger)
    {
        _render = render;
        _qr = qr;
        _barcode = barcode;
        _env = env;
        _logger = logger;
    }

    /// <summary>
    /// Builds a single PDF with one page per certificate. <paramref name="verificationUrlFor"/> maps
    /// a certificate to its public verification URL (which the QR and the verification link use).
    /// </summary>
    public async Task<byte[]> BuildAsync(
        IReadOnlyList<Certificate> certificates,
        Func<Certificate, string> verificationUrlFor,
        CertificateExportSettings settings)
    {
        using var stream = new MemoryStream();
        var metadata = new SKDocumentPdfMetadata
        {
            Title = "شهادات",
            Author = "ECert",
            Creator = "ECert",
            Producer = "ECert",
            Creation = DateTime.Now,
            Modified = DateTime.Now
        };

        using (var document = SKDocument.CreatePdf(stream, metadata))
        {
            foreach (var certificate in certificates)
            {
                var model = await _render.BuildAsync(certificate, verificationUrlFor(certificate));
                DrawPage(document, model, settings);
            }
            document.Close();
        }

        return stream.ToArray();
    }

    private void DrawPage(SKDocument document, CertificateRenderModel model, CertificateExportSettings settings)
    {
        var (pageWidth, pageHeight, margin) = ResolvePage(model, settings);

        var canvas = document.BeginPage(pageWidth, pageHeight);
        canvas.Clear(SKColors.White);

        var scaleX = pageWidth / model.CanvasWidth;
        var scaleY = pageHeight / model.CanvasHeight;

        DrawPaperBackground(canvas, model, pageWidth, pageHeight, settings);
        DrawBackgroundImage(canvas, model, pageWidth, pageHeight);
        DrawElements(canvas, model, scaleX, scaleY, margin);

        if (settings.IncludeBleedMarks)
            DrawCropMarks(canvas, pageWidth, pageHeight, margin);

        document.EndPage();
    }

    /// <summary>
    /// Resolves the page size in points. A4 landscape keeps the artwork centred with a uniform
    /// scale so the certificate is never distorted; the template-canvas option makes the page match
    /// the design's pixel size one-to-one.
    /// </summary>
    private static (float Width, float Height, float Margin) ResolvePage(
        CertificateRenderModel model, CertificateExportSettings settings)
    {
        switch (settings.PageSize)
        {
            case CertificatePdfPageSize.A4Portrait:
                return (A4Short, A4Long, 12f);
            case CertificatePdfPageSize.TemplateCanvas:
                return (model.CanvasWidth, model.CanvasHeight, 0f);
            default:
                // The design is landscape in the common case; if it is taller than wide, swap so the
                // page orientation follows the artwork instead of cropping it.
                return model.CanvasWidth >= model.CanvasHeight
                    ? (A4Long, A4Short, 12f)
                    : (A4Short, A4Long, 12f);
        }
    }

    private static void DrawPaperBackground(SKCanvas canvas, CertificateRenderModel model,
        float pageWidth, float pageHeight, CertificateExportSettings settings)
    {
        var paper = new SKRect(0, 0, pageWidth, pageHeight);
        using var fill = new SKPaint { IsAntialias = true, Color = ParseColor(model.BackgroundColor, SKColors.White) };
        canvas.DrawRect(paper, fill);

        if (model.BorderWidth > 0)
        {
            var inset = Math.Min(pageWidth, pageHeight) * 0.02f;
            var border = new SKRect(inset, inset, pageWidth - inset, pageHeight - inset);
            using var stroke = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = model.BorderWidth,
                Color = ParseColor(model.BorderColor, SKColors.Goldenrod)
            };
            canvas.DrawRect(border, stroke);
        }
    }

    /// <summary>
    /// Draws the design's uploaded background artwork stretched to the full page, mirroring the
    /// <c>object-fit: fill</c> used by the on-screen certificate. The page geometry already matches
    /// the canvas aspect ratio, so filling the page keeps the artwork proportional.
    /// </summary>
    private void DrawBackgroundImage(SKCanvas canvas, CertificateRenderModel model, float pageWidth, float pageHeight)
    {
        if (string.IsNullOrWhiteSpace(model.BackgroundImageUrl)) return;

        using var bitmap = LoadImage(model.BackgroundImageUrl);
        if (bitmap == null) return;

        var target = new SKRect(0, 0, pageWidth, pageHeight);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(bitmap, target, paint);
    }

    private void DrawElements(SKCanvas canvas, CertificateRenderModel model, float scaleX, float scaleY, float margin)
    {
        foreach (var element in model.Elements)
        {
            var left = margin + element.X * scaleX;
            var top = margin + element.Y * scaleY;
            var width = element.Width * scaleX;
            var height = element.Height * scaleY;

            var rotation = element.Rotation;
            if (rotation != 0)
            {
                canvas.Save();
                canvas.RotateDegrees(rotation, left + width / 2f, top + height / 2f);
            }

            switch ((element.ElementType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "qr":
                    DrawQr(canvas, element.EncodedValue, new SKRect(left, top, left + width, top + height));
                    break;
                case "barcode":
                    DrawBarcode(canvas, element.EncodedValue, new SKRect(left, top, left + width, top + height));
                    break;
                case "partner_logo":
                case "partner_signature":
                case "instructor_signature":
                case "image":
                    DrawImageContained(canvas, element.ImageSrc, new SKRect(left, top, left + width, top + height));
                    break;
                case "divider":
                    using (var line = new SKPaint
                    {
                        IsAntialias = true,
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 1.5f,
                        Color = ParseColor(element.FontColor, SKColors.Gray)
                    })
                    {
                        canvas.DrawLine(left, top, left + width, top, line);
                    }
                    break;
                default:
                    DrawText(canvas, element, new SKRect(left, top, left + width, top + height), scaleY);
                    break;
            }

            if (rotation != 0) canvas.Restore();
        }
    }

    private void DrawText(SKCanvas canvas, PublicCertificateDesignElementViewModel element, SKRect box, float scale)
    {
        var text = element.Content;
        if (string.IsNullOrWhiteSpace(text)) return;

        var shaper = SocialCardTextShaper.Instance;
        var typeface = string.Equals(element.FontWeight, "700", StringComparison.Ordinal)
                       || string.Equals(element.FontWeight, "800", StringComparison.Ordinal)
            ? shaper.Bold
            : shaper.Regular;

        // The design stores font size in canvas pixels; convert to page points with the same scale
        // used for positions so the text keeps its proportion to the artwork.
        var size = Math.Max(element.FontSize * scale, 5f);
        var color = ParseColor(element.FontColor, SKColors.Black);
        var align = element.TextAlign switch
        {
            "right" => SKTextAlign.Right,
            "left" => SKTextAlign.Left,
            _ => SKTextAlign.Center
        };
        var anchorX = align switch
        {
            SKTextAlign.Right => box.Right,
            SKTextAlign.Left => box.Left,
            _ => box.MidX
        };

        var lines = shaper.Wrap(text, typeface, size, box.Width, maxLines: 4);
        if (lines.Count == 0) return;

        var lineHeight = size * 1.35f;
        // The element box is vertically centred on screen, so centre the text block in the box.
        var blockHeight = lines.Count * lineHeight;
        var firstBaseline = box.MidY - blockHeight / 2f + size;

        for (var i = 0; i < lines.Count; i++)
            shaper.DrawLine(canvas, lines[i], typeface, size, color, anchorX, firstBaseline + i * lineHeight, align);
    }

    private void DrawQr(SKCanvas canvas, string? value, SKRect box)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var matrix = _qr.BuildMatrix(value);
        var size = matrix.GetLength(0);
        if (size == 0) return;

        var module = Math.Min(box.Width, box.Height) / size;
        var drawn = module * size;
        var originX = box.Left + (box.Width - drawn) / 2f;
        var originY = box.Top + (box.Height - drawn) / 2f;

        using var paint = new SKPaint { IsAntialias = false, Color = SKColors.Black };
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (!matrix[y, x]) continue;
                var rect = new SKRect(
                    originX + x * module, originY + y * module,
                    originX + (x + 1) * module, originY + (y + 1) * module);
                canvas.DrawRect(rect, paint);
            }
        }
    }

    private void DrawBarcode(SKCanvas canvas, string? content, SKRect box)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        var bars = CertificateBarcodeService.BuildBars(content, moduleWidth: 1);
        if (bars.Count == 0) return;

        var totalModules = bars.Sum(b => b.Span);
        if (totalModules == 0) return;

        var module = box.Width / totalModules;
        var x = box.Left;
        using var paint = new SKPaint { IsAntialias = false, Color = SKColors.Black };
        foreach (var (span, isBar) in bars)
        {
            var width = span * module;
            if (isBar)
                canvas.DrawRect(new SKRect(x, box.Top, x + width, box.Bottom), paint);
            x += width;
        }
    }

    private void DrawImageContained(SKCanvas canvas, string? imageUrl, SKRect box)
    {
        using var bitmap = LoadImage(imageUrl);
        if (bitmap == null) return;

        var scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
        var width = bitmap.Width * scale;
        var height = bitmap.Height * scale;
        var target = new SKRect(
            box.Left + (box.Width - width) / 2f,
            box.Top + (box.Height - height) / 2f,
            box.Left + (box.Width + width) / 2f,
            box.Top + (box.Height + height) / 2f);

        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(bitmap, target, paint);
    }

    /// <summary>
    /// Loads an image the design references. Site-relative paths (<c>/uploads/...</c>) are read from
    /// the web root; data URIs and absolute URLs are decoded inline. A missing image is skipped so
    /// one broken logo never aborts the whole export.
    /// </summary>
    private SKBitmap? LoadImage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = url.IndexOf(',');
                if (comma < 0) return null;
                var bytes = Convert.FromBase64String(url[(comma + 1)..]);
                return SKBitmap.Decode(bytes);
            }

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return null; // remote artwork is not fetched during export

            var relative = url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(_env.WebRootPath, relative);
            if (!File.Exists(path)) return null;

            using var stream = File.OpenRead(path);
            return SKBitmap.Decode(stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Certificate PDF export skipped an unreadable image: {Url}", url);
            return null;
        }
    }

    private static void DrawCropMarks(SKCanvas canvas, float pageWidth, float pageHeight, float margin)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 0.5f,
            Color = SKColors.Black
        };
        const float mark = 10f;
        // Corner marks: two short lines per corner, offset from the trim edge.
        canvas.DrawLine(0, margin, mark, margin, paint);
        canvas.DrawLine(margin, 0, margin, mark, paint);
        canvas.DrawLine(pageWidth - mark, margin, pageWidth, margin, paint);
        canvas.DrawLine(pageWidth - margin, 0, pageWidth - margin, mark, paint);
        canvas.DrawLine(0, pageHeight - margin, mark, pageHeight - margin, paint);
        canvas.DrawLine(margin, pageHeight - mark, margin, pageHeight, paint);
        canvas.DrawLine(pageWidth - mark, pageHeight - margin, pageWidth, pageHeight - margin, paint);
        canvas.DrawLine(pageWidth - margin, pageHeight - mark, pageWidth - margin, pageHeight, paint);
    }

    private static SKColor ParseColor(string? hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var value = hex.Trim().TrimStart('#');
        if (value.Length != 6) return fallback;
        return uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var rgb)
            ? new SKColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
            : fallback;
    }
}
