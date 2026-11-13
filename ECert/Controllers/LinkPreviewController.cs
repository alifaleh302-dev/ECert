using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Serves the Open Graph preview images ("smart link cards") that WhatsApp, Telegram and other
/// chat apps show when a link is shared. A certificate link gets a card built from the live
/// certificate (trainee, course, status); any other page falls back to the centre's official logo
/// card, so every shared link carries the centre's visual identity.
/// </summary>
[AllowAnonymous]
public class LinkPreviewController : Controller
{
    private readonly ECertDbContext _db;
    private readonly SocialCardService _cards;
    private readonly CertificateSecurityService _certificateSecurity;
    private readonly ILogger<LinkPreviewController> _logger;

    public LinkPreviewController(ECertDbContext db, SocialCardService cards,
        CertificateSecurityService certificateSecurity, ILogger<LinkPreviewController> logger)
    {
        _db = db;
        _cards = cards;
        _certificateSecurity = certificateSecurity;
        _logger = logger;
    }

    /// <summary>Preview image for a public certificate link (/v/{publicId}?sig=...).</summary>
    [HttpGet("/share/certificate/{publicId}.png")]
    [OutputCache(Duration = 900)]
    public async Task<IActionResult> Certificate(string publicId, string? sig)
    {
        var branding = await LoadBrandingAsync();
        var content = new SocialCardContent
        {
            SiteName = branding.SiteName,
            LogoUrl = branding.LogoUrl,
            PrimaryColor = branding.PrimaryColor,
            AccentColor = branding.AccentColor
        };

        if (string.IsNullOrWhiteSpace(publicId) || !_certificateSecurity.VerifySignature(publicId, sig))
            return Png(_cards.Render(content));

        var certificate = await _db.Certificates.AsNoTracking()
            .FirstOrDefaultAsync(c => c.PublicId == publicId.Trim());

        if (certificate == null)
            return Png(_cards.Render(content));

        var isValid = string.Equals(certificate.Status, "Valid", StringComparison.OrdinalIgnoreCase);
        content = content with
        {
            Title = certificate.CourseNameArabic ?? certificate.CourseName ?? "شهادة",
            Subtitle = certificate.TraineeNameArabic ?? certificate.TraineeName,
            StatusText = isValid ? "شهادة سارية وموثّقة" : "شهادة غير قابلة للاعتماد",
            StatusIsPositive = isValid,
            MetaLeft = certificate.IssueDate.ToString("yyyy-MM-dd"),
            MetaRight = certificate.CertificateNumber
        };

        return Png(_cards.Render(content));
    }

    /// <summary>Fallback preview image for any other page: the centre's official logo card.</summary>
    [HttpGet("/share/site.png")]
    [OutputCache(Duration = 3600)]
    public async Task<IActionResult> Site()
    {
        var branding = await LoadBrandingAsync();
        var content = new SocialCardContent
        {
            SiteName = branding.SiteName,
            LogoUrl = branding.LogoUrl,
            PrimaryColor = branding.PrimaryColor,
            AccentColor = branding.AccentColor,
            Subtitle = branding.Tagline
        };

        return Png(_cards.Render(content));
    }

    private async Task<Branding> LoadBrandingAsync()
    {
        var settings = await _db.SiteSettings.AsNoTracking().ToListAsync();
        string Value(string key) => settings.FirstOrDefault(s => s.Key == key)?.Value ?? string.Empty;

        var siteName = Value("SiteName");
        if (string.IsNullOrWhiteSpace(siteName)) siteName = "مركز التدريب";

        var tagline = Value("MetaDescription");
        if (string.IsNullOrWhiteSpace(tagline))
            tagline = "مركز تدريبي معتمد يقدم دورات احترافية وشهادات موثّقة إلكترونياً.";

        var theme = await _db.ThemeSettings.AsNoTracking().FirstOrDefaultAsync();
        var primary = string.IsNullOrWhiteSpace(theme?.NavbarColor) ? "#0f172a" : theme!.NavbarColor!;
        var accent = string.IsNullOrWhiteSpace(theme?.SecondaryColor) ? "#c9a227" : theme!.SecondaryColor!;

        return new Branding(siteName, Value("LogoUrl"), tagline, primary, accent);
    }

    private IActionResult Png(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            _logger.LogWarning("Social card rendering produced no image; serving a placeholder.");
            return StatusCode(StatusCodes.Status500InternalServerError);
        }

        Response.Headers.CacheControl = "public, max-age=900";
        return File(bytes, "image/png");
    }

    private sealed record Branding(string SiteName, string LogoUrl, string Tagline, string PrimaryColor, string AccentColor);
}
