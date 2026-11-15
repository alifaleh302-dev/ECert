using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Turns a <see cref="Certificate"/> into a render-ready <see cref="CertificateRenderModel"/>: it
/// resolves the design that applies to the certificate's course, substitutes every field value, and
/// produces the QR/barcode/logo/signature images. Both the public verification page and the PDF
/// export go through this service, so what the administrator sees on screen is exactly what the
/// printed PDF contains.
/// </summary>
public sealed class CertificateRenderService
{
    private readonly ECertDbContext _db;
    private readonly CertificateDesignService _designs;
    private readonly CertificateQrService _qr;
    private readonly CertificateBarcodeService _barcode;

    public CertificateRenderService(
        ECertDbContext db,
        CertificateDesignService designs,
        CertificateQrService qr,
        CertificateBarcodeService barcode)
    {
        _db = db;
        _designs = designs;
        _qr = qr;
        _barcode = barcode;
    }

    public async Task<CertificateRenderModel> BuildAsync(Certificate certificate, string verificationUrl)
    {
        var design = await _designs.GetForCourseAsync(certificate.Registration?.CourseId);
        if (design == null)
        {
            return new CertificateRenderModel
            {
                Certificate = certificate,
                VerificationUrl = verificationUrl,
                HasDesign = false
            };
        }

        var partners = certificate.Registration?.Course?.Partners
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.CoursePartnerId)
            .ToList() ?? new List<CoursePartner>();
        var signatureUrl = await ResolveSignatureUrlAsync(certificate.Registration?.Course);

        var elements = design.Elements
            .Where(e => e.IsVisible)
            .OrderBy(e => e.SortOrder)
            .Select(e =>
            {
                var isQr = string.Equals(e.ElementType, "qr", StringComparison.OrdinalIgnoreCase);
                var isBarcode = string.Equals(e.ElementType, "barcode", StringComparison.OrdinalIgnoreCase);
                var encoded = isQr
                    ? verificationUrl
                    : isBarcode
                        ? CertificateDesignService.ResolveBarcodeValue(e.FieldKey, certificate, verificationUrl)
                        : null;

                return new PublicCertificateDesignElementViewModel
                {
                    ElementType = e.ElementType,
                    Content = string.Equals(e.ElementType, "field", StringComparison.OrdinalIgnoreCase)
                        ? CertificateDesignService.ResolveFieldValue(e.FieldKey, certificate, verificationUrl)
                        : e.Content,
                    EncodedValue = encoded,
                    ImageSrc = isQr
                        ? _qr.BuildDataUri(encoded!, QrModulePixels(e))
                        : isBarcode
                            ? _barcode.BuildDataUri(encoded!, BarcodeModuleWidth(e), BarcodeHeight(e))
                            : ResolvePartnerArtwork(e, partners)
                                ?? (string.Equals(e.ElementType, "instructor_signature", StringComparison.OrdinalIgnoreCase)
                                    ? signatureUrl
                                    : null),
                    X = e.X,
                    Y = e.Y,
                    Width = e.Width,
                    Height = e.Height,
                    FontSize = e.FontSize,
                    FontFamily = e.FontFamily,
                    FontColor = e.FontColor,
                    FontWeight = e.FontWeight,
                    TextAlign = e.TextAlign,
                    IsItalic = e.IsItalic,
                    IsUnderline = e.IsUnderline,
                    LineHeight = e.LineHeight,
                    LetterSpacing = e.LetterSpacing,
                    ZIndex = e.ZIndex,
                    Rotation = e.Rotation,
                    Opacity = e.Opacity
                };
            })
            .ToList();

        return new CertificateRenderModel
        {
            Certificate = certificate,
            VerificationUrl = verificationUrl,
            HasDesign = true,
            CanvasWidth = design.CanvasWidth,
            CanvasHeight = design.CanvasHeight,
            BackgroundColor = design.BackgroundColor,
            BorderColor = design.BorderColor,
            BorderWidth = design.BorderWidth,
            BorderRadius = design.BorderRadius,
            BackgroundImageUrl = design.BackgroundImageUrl,
            Elements = elements
        };
    }

    /// <summary>
    /// Returns the signature image of the first active instructor assigned to the course, or null
    /// when none of them has one. Falls back to the course's primary instructor because legacy
    /// courses may have no <c>CourseInstructors</c> join row.
    /// </summary>
    private async Task<string?> ResolveSignatureUrlAsync(Course? course)
    {
        if (course == null) return null;

        var signature = await _db.CourseInstructors
            .AsNoTracking()
            .Where(ci => ci.CourseId == course.CourseId
                && ci.Instructor != null
                && ci.Instructor.IsActive
                && ci.Instructor.SignatureImageUrl != null)
            .OrderBy(ci => ci.SortOrder)
            .ThenBy(ci => ci.CourseInstructorId)
            .Select(ci => ci.Instructor!.SignatureImageUrl)
            .FirstOrDefaultAsync();

        if (signature != null) return signature;

        if (course.InstructorId == 0) return null;

        return await _db.Instructors
            .AsNoTracking()
            .Where(i => i.InstructorId == course.InstructorId && i.IsActive && i.SignatureImageUrl != null)
            .Select(i => i.SignatureImageUrl)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Resolves the artwork for a partner element. The element's field key selects which partner
    /// slot it renders (0 = first partner), so a template can show several organisations.
    /// </summary>
    private static string? ResolvePartnerArtwork(CertificateDesignElement element, IReadOnlyList<CoursePartner> partners)
    {
        var type = element.ElementType?.Trim().ToLowerInvariant();
        if (type != "partner_logo" && type != "partner_signature") return null;

        var slot = CertificateDesignService.ParsePartnerSlot(element.FieldKey);
        if (slot >= partners.Count) return null;

        return type == "partner_logo" ? partners[slot].LogoUrl : partners[slot].SignatureUrl;
    }

    /// <summary>Chooses a module size that keeps the rendered QR crisp without producing a huge PNG.</summary>
    private static int QrModulePixels(CertificateDesignElement element)
        => Math.Clamp(Math.Max(element.Width, element.Height) / 25, 3, 14);

    /// <summary>
    /// Code 128's width depends on the encoded text length, so the bar width is derived from the
    /// element's pixel width; the SVG scales the bars to fill the element box regardless.
    /// </summary>
    private static int BarcodeModuleWidth(CertificateDesignElement element)
        => Math.Clamp(element.Width / 60, 1, 4);

    private static int BarcodeHeight(CertificateDesignElement element)
        => Math.Clamp(element.Height, 24, 200);
}
