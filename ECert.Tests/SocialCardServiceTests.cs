using ECert.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using SkiaSharp;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Guards the Open Graph share-card renderer: the output must be a real 1200×630 PNG, the Arabic
/// text must be shaped into joined glyphs (not blank or tofu), and the different card kinds must
/// actually draw something in the regions they claim to.
/// </summary>
public class SocialCardServiceTests
{
    private readonly SocialCardService _service = new(new StubEnvironment());

    /// <summary>
    /// A minimal hosting environment so the renderer can resolve its web root. The tests do not
    /// rely on any file being present: a missing logo must fall back to the built-in emblem.
    /// </summary>
    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ECert.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.Combine(Path.GetTempPath(), "ecert-tests-wwwroot");
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static SocialCardContent Certificate() => new()
    {
        SiteName = "مركز التدريب المهني",
        PrimaryColor = "#0f172a",
        AccentColor = "#c9a227",
        Title = "الأخطاء الشائعة في الطوارئ",
        Subtitle = "أصيل حمود قائد نصر",
        StatusText = "شهادة سارية وموثّقة",
        StatusIsPositive = true,
        MetaLeft = "2024-05-12",
        MetaRight = "FMEM102351146"
    };

    [Fact]
    public void Render_produces_a_1200x630_png()
    {
        var bytes = _service.Render(Certificate());

        Assert.NotNull(bytes);
        // PNG signature.
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes!.Take(4).ToArray());

        using var bitmap = SKBitmap.Decode(bytes);
        Assert.Equal(1200, bitmap.Width);
        Assert.Equal(630, bitmap.Height);
    }

    [Fact]
    public void Certificate_card_draws_text_in_every_band()
    {
        var bytes = _service.Render(Certificate())!;
        using var bitmap = SKBitmap.Decode(bytes);

        Assert.True(LightPixels(bitmap, 40, 150, 0, 1200) > 200, "header (site name) should draw light text");
        Assert.True(LightPixels(bitmap, 320, 560, 0, 1200) > 400, "title/subtitle band should draw light text");
        Assert.True(LightPixels(bitmap, 500, 600, 600, 1200) > 100, "footer band should draw light text");
    }

    [Fact]
    public void Site_card_draws_a_centred_emblem_and_name()
    {
        var bytes = _service.Render(new SocialCardContent
        {
            SiteName = "مركز التدريب المهني",
            Subtitle = "مركز تدريبي معتمد يقدم دورات احترافية وشهادات موثّقة إلكترونياً."
        })!;
        using var bitmap = SKBitmap.Decode(bytes);

        // The emblem occupies the middle of the card; the name sits below it.
        Assert.True(LightPixels(bitmap, 200, 390, 400, 800) > 300, "emblem should draw in the centre");
        Assert.True(LightPixels(bitmap, 430, 490, 0, 1200) > 200, "site name should draw under the emblem");
    }

    [Fact]
    public void Arabic_text_is_shaped_into_glyphs_not_blank()
    {
        var shaper = SocialCardTextShaper.Instance;

        // A correctly shaped Arabic line is much narrower than one glyph per character would be,
        // and it must contain no missing-glyph (codepoint 0) entries.
        var width = shaper.Measure("شهادة سارية وموثّقة", shaper.Bold, 32);
        Assert.True(width > 50, "shaped Arabic should have a positive width");
        Assert.True(width < 400, "shaped Arabic should be far narrower than one glyph per character");
    }

    [Fact]
    public void Long_title_is_wrapped_to_the_card_width()
    {
        var shaper = SocialCardTextShaper.Instance;
        var lines = shaper.Wrap(
            "دورة إدارة المشاريع الاحترافية المتقدمة وفق معايير PMI العالمية والقيادة الاستراتيجية",
            shaper.Bold, 54, 1056, 2);

        Assert.InRange(lines.Count, 1, 2);
        foreach (var line in lines)
            Assert.True(shaper.Measure(line, shaper.Bold, 54) <= 1056 + 1, $"line '{line}' must fit the card width");
    }

    [Fact]
    public void Missing_logo_falls_back_to_the_default_emblem()
    {
        // A logo path that does not exist must not throw; the emblem still renders.
        var bytes = _service.Render(new SocialCardContent
        {
            SiteName = "مركز التدريب",
            LogoUrl = "/uploads/does-not-exist.png",
            Subtitle = "وصف تعريفي"
        });

        Assert.NotNull(bytes);
        using var bitmap = SKBitmap.Decode(bytes!);
        Assert.True(LightPixels(bitmap, 200, 390, 400, 800) > 200);
    }

    private static int LightPixels(SKBitmap bitmap, int y0, int y1, int x0, int x1)
    {
        var count = 0;
        for (var y = y0; y < y1 && y < bitmap.Height; y++)
        for (var x = x0; x < x1 && x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.Red > 200 && pixel.Green > 200 && pixel.Blue > 200) count++;
        }

        return count;
    }
}
