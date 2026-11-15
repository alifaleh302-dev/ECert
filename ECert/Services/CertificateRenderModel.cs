using ECert.Models;
using ECert.Models.ViewModels;

namespace ECert.Services;

/// <summary>
/// The resolved, render-ready form of a single certificate: the design that applies to its course
/// with every field value and artwork path already substituted. Produced by
/// <see cref="CertificateRenderService"/> so the on-screen certificate and the exported PDF are
/// built from exactly the same data and cannot drift apart.
/// </summary>
public sealed class CertificateRenderModel
{
    public Certificate Certificate { get; init; } = null!;
    public string VerificationUrl { get; init; } = string.Empty;

    public bool HasDesign { get; init; }
    public int CanvasWidth { get; init; } = 1120;
    public int CanvasHeight { get; init; } = 792;
    public string BackgroundColor { get; init; } = "#fffdf7";
    public string BorderColor { get; init; } = "#c9a227";
    public int BorderWidth { get; init; } = 12;
    public int BorderRadius { get; init; } = 8;
    public string? BackgroundImageUrl { get; init; }

    /// <summary>Elements in draw order (already filtered to visible ones and ordered by SortOrder).</summary>
    public IReadOnlyList<PublicCertificateDesignElementViewModel> Elements { get; init; }
        = Array.Empty<PublicCertificateDesignElementViewModel>();
}
