using SkiaSharp;

namespace ECert.Services;

/// <summary>
/// The data a share card renders. Kept free of EF entities so the renderer stays testable and the
/// card only ever shows what the caller decides to publish.
/// </summary>
public sealed record SocialCardContent
{
    public string SiteName { get; init; } = "مركز التدريب";
    public string? LogoUrl { get; init; }
    public string PrimaryColor { get; init; } = "#0f172a";
    public string AccentColor { get; init; } = "#c9a227";

    /// <summary>Certificate cards set these; the generic site card leaves them null.</summary>
    public string? Title { get; init; }
    public string? Subtitle { get; init; }
    public string? StatusText { get; init; }
    public bool StatusIsPositive { get; init; } = true;
    public string? MetaLeft { get; init; }
    public string? MetaRight { get; init; }
}

/// <summary>
/// Draws the 1200×630 Open Graph images that chat apps show when a link is shared. WhatsApp and
/// Telegram do not render SVG, so the cards are rasterised PNGs. Arabic text is shaped through
/// HarfBuzz (see <see cref="SocialCardTextShaper"/>) because Skia's plain text drawing would emit
/// disconnected letter forms.
/// </summary>
public sealed class SocialCardService
{
    public const int Width = 1200;
    public const int Height = 630;

    private const float Margin = 72f;

    private readonly SocialCardTextShaper _text = SocialCardTextShaper.Instance;
    private readonly string _webRoot;

    public SocialCardService(IWebHostEnvironment environment)
    {
        _webRoot = environment.WebRootPath
            ?? Path.Combine(environment.ContentRootPath, "wwwroot");
    }

    /// <summary>Renders the card as PNG bytes. Returns null only if the canvas cannot be created.</summary>
    public byte[]? Render(SocialCardContent content)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface == null) return null;

        var canvas = surface.Canvas;
        var background = ParseColor(content.PrimaryColor, new SKColor(15, 23, 42));
        var accent = ParseColor(content.AccentColor, new SKColor(201, 162, 39));
        var onDark = SKColors.White;

        DrawBackground(canvas, background);
        DrawFrame(canvas, accent);
        DrawHeader(canvas, content, accent, onDark);

        if (string.IsNullOrWhiteSpace(content.Title))
            DrawSiteBody(canvas, content, accent, onDark);
        else
            DrawCertificateBody(canvas, content, accent, onDark);

        DrawFooter(canvas, content, onDark);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        return data.ToArray();
    }

    private static void DrawBackground(SKCanvas canvas, SKColor baseColor)
    {
        canvas.Clear(baseColor);
        using var paint = new SKPaint { IsAntialias = true };
        paint.Shader = SKShader.CreateLinearGradient(
            new SKPoint(0, 0),
            new SKPoint(Width, Height),
            new[] { Lighten(baseColor, 0.16f), Darken(baseColor, 0.34f) },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRect(SKRect.Create(0, 0, Width, Height), paint);

        // A soft accent glow behind the upper-right corner gives the card depth.
        using var glow = new SKPaint { IsAntialias = true };
        glow.Shader = SKShader.CreateRadialGradient(
            new SKPoint(Width - 140, 120),
            420,
            new[] { new SKColor(255, 255, 255, 26), new SKColor(255, 255, 255, 0) },
            new[] { 0f, 1f },
            SKShaderTileMode.Clamp);
        canvas.DrawRect(SKRect.Create(0, 0, Width, Height), glow);
    }

    private static void DrawFrame(SKCanvas canvas, SKColor accent)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            Color = accent.WithAlpha(150)
        };
        canvas.DrawRoundRect(SKRect.Create(24, 24, Width - 48, Height - 48), 22, 22, paint);
    }

    private void DrawHeader(SKCanvas canvas, SocialCardContent content, SKColor accent, SKColor onDark)
    {
        var logo = LoadLogo(content.LogoUrl);
        var textX = Width - Margin;
        var logoSize = 64f;

        // The emblem sits at the visual "end" of an RTL line (left) while the name anchors the
        // "start" (right), which keeps the header balanced for Arabic readers.
        var emblemRect = SKRect.Create(Margin, 46, logoSize, logoSize);
        if (logo != null)
        {
            DrawImageContained(canvas, logo, emblemRect);
            logo.Dispose();
        }
        else
        {
            DrawDefaultEmblem(canvas, emblemRect, accent, onDark);
        }

        _text.DrawLine(canvas, content.SiteName, _text.Bold, 30, onDark, textX, 86, SKTextAlign.Right);
        _text.DrawLine(canvas, "شهادة موثّقة إلكترونياً", _text.Regular, 19,
            onDark.WithAlpha(190), textX, 118, SKTextAlign.Right);

        using var rule = new SKPaint { IsAntialias = true, Color = accent.WithAlpha(120), StrokeWidth = 2 };
        canvas.DrawLine(Margin, 158, Width - Margin, 158, rule);
    }

    private void DrawCertificateBody(SKCanvas canvas, SocialCardContent content, SKColor accent, SKColor onDark)
    {
        var centerX = Width / 2f;

        DrawSeal(canvas, centerX, 250, content.StatusIsPositive ? new SKColor(34, 197, 94) : accent);

        _text.DrawLine(canvas, content.StatusText ?? "شهادة سارية وموثّقة", _text.Bold, 32,
            content.StatusIsPositive ? new SKColor(134, 239, 172) : accent,
            centerX, 348, SKTextAlign.Center);

        var titleLines = _text.Wrap(content.Title ?? string.Empty, _text.Bold, 54, Width - Margin * 2, 2);
        for (var i = 0; i < titleLines.Count; i++)
            _text.DrawLine(canvas, titleLines[i], _text.Bold, 54, onDark, centerX, 424 + i * 66, SKTextAlign.Center);

        if (!string.IsNullOrWhiteSpace(content.Subtitle))
        {
            var subtitleBaseline = titleLines.Count > 1 ? 566f : 500f;
            _text.DrawWrapped(canvas, content.Subtitle, _text.Regular, 30, onDark.WithAlpha(205),
                centerX, subtitleBaseline, 42, Width - Margin * 2, 1, SKTextAlign.Center);
        }
    }

    private void DrawSiteBody(SKCanvas canvas, SocialCardContent content, SKColor accent, SKColor onDark)
    {
        var centerX = Width / 2f;

        // The generic card leads with the centre's emblem so the brand is unmistakable in the chat
        // thumbnail, then the tagline underneath.
        var logo = LoadLogo(content.LogoUrl);
        var emblemSize = 190f;
        var emblemRect = SKRect.Create(centerX - emblemSize / 2f, 196, emblemSize, emblemSize);
        if (logo != null)
        {
            DrawImageContained(canvas, logo, emblemRect);
            logo.Dispose();
        }
        else
        {
            DrawDefaultEmblem(canvas, emblemRect, accent, onDark);
        }

        _text.DrawLine(canvas, content.SiteName, _text.Bold, 44, onDark, centerX, 462, SKTextAlign.Center);

        if (!string.IsNullOrWhiteSpace(content.Subtitle))
        {
            _text.DrawWrapped(canvas, content.Subtitle, _text.Regular, 28, onDark.WithAlpha(205),
                centerX, 512, 40, Width - Margin * 2, 2, SKTextAlign.Center);
        }
    }

    private void DrawFooter(SKCanvas canvas, SocialCardContent content, SKColor onDark)
    {
        var metaLeft = content.MetaLeft;
        var metaRight = content.MetaRight;
        if (string.IsNullOrWhiteSpace(metaLeft) && string.IsNullOrWhiteSpace(metaRight)) return;

        using var pill = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 28) };
        canvas.DrawRoundRect(SKRect.Create(Margin, Height - 116, Width - Margin * 2, 60), 18, 18, pill);

        if (!string.IsNullOrWhiteSpace(metaRight))
            _text.DrawLine(canvas, metaRight, _text.Bold, 26, onDark, Width - Margin - 24, Height - 76, SKTextAlign.Right);
        if (!string.IsNullOrWhiteSpace(metaLeft))
            _text.DrawLine(canvas, metaLeft, _text.Regular, 24, onDark.WithAlpha(210), Margin + 24, Height - 76, SKTextAlign.Left);
    }

    /// <summary>
    /// Draws a neutral emblem (a graduation cap in a disc) used when the centre has not uploaded a
    /// logo, so the card never falls back to an empty box.
    /// </summary>
    private static void DrawDefaultEmblem(SKCanvas canvas, SKRect box, SKColor accent, SKColor onDark)
    {
        var cx = box.MidX;
        var cy = box.MidY;
        var radius = Math.Min(box.Width, box.Height) / 2f;

        using var disc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(255, 255, 255, 30) };
        canvas.DrawCircle(cx, cy, radius, disc);

        using var ring = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(3f, radius * 0.06f),
            Color = accent
        };
        canvas.DrawCircle(cx, cy, radius, ring);

        // Mortarboard: a rhombus board with a tassel dot, scaled to the box.
        var w = radius * 1.1f;
        var h = radius * 0.5f;
        using var board = new SKPath();
        board.MoveTo(cx, cy - h);
        board.LineTo(cx + w, cy - h * 0.15f);
        board.LineTo(cx, cy + h * 0.7f);
        board.LineTo(cx - w, cy - h * 0.15f);
        board.Close();
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = onDark };
        canvas.DrawPath(board, fill);

        using var tassel = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = accent };
        canvas.DrawCircle(cx + w * 0.92f, cy + h * 0.55f, radius * 0.11f, tassel);
    }

    /// <summary>Draws a circular badge with a check mark (or exclamation) inside.</summary>
    private static void DrawSeal(SKCanvas canvas, float cx, float cy, SKColor color)
    {
        using var ring = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 6, Color = color };
        canvas.DrawCircle(cx, cy, 46, ring);

        using var disc = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color.WithAlpha(46) };
        canvas.DrawCircle(cx, cy, 46, disc);

        using var mark = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 8,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            Color = color
        };
        using var path = new SKPath();
        path.MoveTo(cx - 20, cy + 2);
        path.LineTo(cx - 6, cy + 16);
        path.LineTo(cx + 20, cy - 14);
        canvas.DrawPath(path, mark);
    }

    /// <summary>Loads a logo from the web root, or null when it is missing or not a raster image.</summary>
    private SKBitmap? LoadLogo(string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;

        var relative = logoUrl.Split('?')[0].TrimStart('/');
        if (relative.Length == 0) return null;

        var fullPath = Path.GetFullPath(Path.Combine(_webRoot, relative));
        if (!fullPath.StartsWith(Path.GetFullPath(_webRoot), StringComparison.Ordinal)) return null;
        if (!File.Exists(fullPath)) return null;

        try
        {
            using var stream = File.OpenRead(fullPath);
            return SKBitmap.Decode(stream);
        }
        catch
        {
            return null;
        }
    }

    private static void DrawImageContained(SKCanvas canvas, SKBitmap image, SKRect target)
    {
        if (image.Width == 0 || image.Height == 0) return;

        var scale = Math.Min(target.Width / image.Width, target.Height / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        var dest = SKRect.Create(target.Left + (target.Width - w) / 2f, target.Top + (target.Height - h) / 2f, w, h);

        using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(image, dest, paint);
    }

    private static SKColor ParseColor(string? value, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return SKColor.TryParse(value.Trim(), out var parsed) ? parsed : fallback;
    }

    private static SKColor Lighten(SKColor color, float amount) => new(
        (byte)Math.Clamp(color.Red + (255 - color.Red) * amount, 0, 255),
        (byte)Math.Clamp(color.Green + (255 - color.Green) * amount, 0, 255),
        (byte)Math.Clamp(color.Blue + (255 - color.Blue) * amount, 0, 255));

    private static SKColor Darken(SKColor color, float amount) => new(
        (byte)Math.Clamp(color.Red * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.Green * (1 - amount), 0, 255),
        (byte)Math.Clamp(color.Blue * (1 - amount), 0, 255));
}
