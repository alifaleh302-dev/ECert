using System.Collections.Concurrent;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace ECert.Services;

/// <summary>
/// Loads the bundled Arabic/Latin fonts once and renders text with correct Arabic shaping. The
/// social share cards need joined Arabic glyphs, which plain Skia text drawing does not produce,
/// so every string goes through HarfBuzz and is drawn as a positioned glyph run.
/// </summary>
public sealed class SocialCardTextShaper : IDisposable
{
    private static readonly Lazy<SocialCardTextShaper> Lazy = new(() => new SocialCardTextShaper());

    public static SocialCardTextShaper Instance => Lazy.Value;

    private readonly SKTypeface _regular;
    private readonly SKTypeface _bold;
    private readonly ConcurrentDictionary<(SKTypeface, float), SKShaper> _shapers = new();

    private SocialCardTextShaper()
    {
        _regular = LoadTypeface("Tajawal-Regular.ttf");
        _bold = LoadTypeface("Tajawal-Bold.ttf");
    }

    public SKTypeface Regular => _regular;

    public SKTypeface Bold => _bold;

    private static SKTypeface LoadTypeface(string fileName)
    {
        var assembly = typeof(SocialCardTextShaper).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            return SKTypeface.FromFamilyName("sans-serif");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return SKTypeface.FromData(SKData.CreateCopy(memory.ToArray()));
    }

    private SKShaper ShaperFor(SKTypeface typeface)
    {
        var key = (typeface, typeface.FontStyle.Weight);
        return _shapers.GetOrAdd(key, t => new SKShaper(t.Item1));
    }

    /// <summary>Measures the shaped width of a single line, in pixels.</summary>
    public float Measure(string text, SKTypeface typeface, float size)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        var shaper = ShaperFor(typeface);
        using var paint = new SKPaint { TextSize = size };
        return shaper.Shape(text, paint).Width;
    }

    /// <summary>
    /// Draws one line with the given anchor. <paramref name="x"/> is the left edge when
    /// <paramref name="align"/> is Left, the centre when Centre, the right edge when Right.
    /// The baseline sits at <paramref name="baseline"/>.
    /// </summary>
    public void DrawLine(SKCanvas canvas, string text, SKTypeface typeface, float size, SKColor color,
        float x, float baseline, SKTextAlign align = SKTextAlign.Left, float maxWidth = 0f)
    {
        if (string.IsNullOrEmpty(text)) return;

        var shaper = ShaperFor(typeface);
        using var paint = new SKPaint { IsAntialias = true, Color = color, TextSize = size };
        var shaped = shaper.Shape(text, paint);
        if (shaped.Codepoints.Length == 0) return;

        var width = shaped.Width;
        var originX = align switch
        {
            SKTextAlign.Center => x - width / 2f,
            SKTextAlign.Right => x - width,
            _ => x
        };

        var clipped = maxWidth > 0f && width > maxWidth;
        if (clipped)
        {
            canvas.Save();
            canvas.ClipRect(SKRect.Create(originX, baseline - size * 1.1f, maxWidth, size * 1.7f), SKClipOperation.Intersect);
        }

        canvas.DrawShapedText(shaper, text, originX, baseline, paint);

        if (clipped) canvas.Restore();
    }

    /// <summary>
    /// Draws up to <paramref name="maxLines"/> lines, wrapping on spaces to fit
    /// <paramref name="maxWidth"/>. Returns the number of lines drawn.
    /// </summary>
    public int DrawWrapped(SKCanvas canvas, string text, SKTypeface typeface, float size, SKColor color,
        float x, float firstBaseline, float lineHeight, float maxWidth, int maxLines, SKTextAlign align = SKTextAlign.Left)
    {
        var lines = Wrap(text, typeface, size, maxWidth, maxLines);
        for (var i = 0; i < lines.Count; i++)
            DrawLine(canvas, lines[i], typeface, size, color, x, firstBaseline + i * lineHeight, align);
        return lines.Count;
    }

    /// <summary>Greedy word wrap, trimming an overflowing last line with an ellipsis.</summary>
    public List<string> Wrap(string text, SKTypeface typeface, float size, float maxWidth, int maxLines)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text) || maxLines <= 0) return result;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = new System.Text.StringBuilder();
        var overflowed = false;

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (Measure(candidate, typeface, size) <= maxWidth || current.Length == 0)
            {
                current.Clear();
                current.Append(candidate);
                continue;
            }

            result.Add(current.ToString());
            current.Clear();
            current.Append(word);

            if (result.Count == maxLines)
            {
                overflowed = true;
                break;
            }
        }

        if (!overflowed && current.Length > 0) result.Add(current.ToString());
        if (result.Count > maxLines) result = result.Take(maxLines).ToList();

        if (overflowed || result.Count == maxLines)
        {
            var last = result[^1];
            while (last.Length > 1 && Measure(last + "…", typeface, size) > maxWidth)
                last = last[..^1];
            result[^1] = last + "…";
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var shaper in _shapers.Values) shaper.Dispose();
        _shapers.Clear();
        _regular.Dispose();
        _bold.Dispose();
    }
}
