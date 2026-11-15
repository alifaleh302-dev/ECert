using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

/// <summary>
/// The single row (Id = 1) of operator-editable certificate-export configuration. The values are
/// read at runtime by <see cref="Services.CertificateExportSettingsService"/>, so a SuperAdmin can
/// change the export limits from <c>/CertificateExportSettings</c> without a redeploy. The row is
/// seeded with safe defaults on first boot.
/// </summary>
public class CertificateExportSettings
{
    public const int SingletonId = 1;

    [Key]
    public int CertificateExportSettingsId { get; set; } = SingletonId;

    /// <summary>
    /// When true, a visitor of the public verification page may download the certificate as a PDF.
    /// Off by default because an open download endpoint consumes server resources.
    /// </summary>
    [Display(Name = "السماح للعامة بتحميل الشهادات PDF")]
    public bool AllowPublicDownload { get; set; }

    /// <summary>
    /// Largest number of certificates a single export request may produce. Exports render on the
    /// request thread, so this ceiling protects the server's memory and CPU; it is tuned per
    /// deployment to match the host's resources.
    /// </summary>
    [Range(1, 500, ErrorMessage = "الحد الأقصى للتصدير يجب أن يكون بين 1 و 500.")]
    [Display(Name = "الحد الأقصى للشهادات في الطلب الواحد")]
    public int MaxCertificatesPerExport { get; set; } = 100;

    /// <summary>Page size of the generated PDF: an A4 sheet or the template canvas itself.</summary>
    [Display(Name = "مقاس صفحة PDF")]
    public CertificatePdfPageSize PageSize { get; set; } = CertificatePdfPageSize.A4Landscape;

    /// <summary>Adds crop marks and a bleed margin around the artwork for professional printing.</summary>
    [Display(Name = "علامات القص والحدود (Bleed)")]
    public bool IncludeBleedMarks { get; set; }

    /// <summary>Optional prefix prepended to the downloaded PDF file name.</summary>
    [StringLength(40)]
    [Display(Name = "بادئة اسم ملف PDF")]
    public string? FileNamePrefix { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    [StringLength(150)]
    public string? UpdatedBy { get; set; }
}

/// <summary>Physical page size of the generated certificate PDF.</summary>
public enum CertificatePdfPageSize
{
    /// <summary>A4 landscape sheet (842 × 595 pt) — the default for on-screen printing.</summary>
    A4Landscape = 0,

    /// <summary>A4 portrait sheet (595 × 842 pt).</summary>
    A4Portrait = 1,

    /// <summary>The template canvas size itself, so the page matches the design exactly.</summary>
    TemplateCanvas = 2
}
