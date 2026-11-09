using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

/// <summary>
/// Represents the visual layer shown on a verified certificate. It is independent of historical
/// certificate-template artifacts so the published design remains the source for public verification.
/// </summary>
public class CertificateDesign
{
    [Key]
    public int CertificateDesignId { get; set; }
    [Required, StringLength(120)]
    public string Name { get; set; } = "القالب البصري الأساسي";
    [Required, StringLength(80)]
    public string DesignKey { get; set; } = "default";
    public bool IsPublished { get; set; }
    [Range(800, 1600)]
    public int CanvasWidth { get; set; } = 1120;
    [Range(500, 1200)]
    public int CanvasHeight { get; set; } = 792;
    [Required, StringLength(7)]
    public string BackgroundColor { get; set; } = "#fffdf7";
    [Required, StringLength(7)]
    public string BorderColor { get; set; } = "#c9a227";
    [Range(0, 24)]
    public int BorderWidth { get; set; } = 12;
    [Range(0, 80)]
    public int BorderRadius { get; set; } = 8;
    /// <summary>Optional uploaded background image (full-bleed artwork) drawn behind every element.</summary>
    [StringLength(400)]
    public string? BackgroundImageUrl { get; set; }
    [StringLength(100)]
    public string? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public ICollection<CertificateDesignElement> Elements { get; set; } = new List<CertificateDesignElement>();
}

public class CertificateDesignElement
{
    [Key]
    public int CertificateDesignElementId { get; set; }
    [Required]
    public int CertificateDesignId { get; set; }
    public CertificateDesign? Design { get; set; }
    [Required, StringLength(20)]
    public string ElementType { get; set; } = "field";
    [Required, StringLength(50)]
    public string FieldKey { get; set; } = "trainee_name";
    [StringLength(1000)]
    public string Content { get; set; } = string.Empty;
    [Range(0, 1600)]
    public int X { get; set; } = 100;
    [Range(0, 1200)]
    public int Y { get; set; } = 100;
    [Range(20, 1600)]
    public int Width { get; set; } = 500;
    [Range(20, 1200)]
    public int Height { get; set; } = 64;
    [Range(8, 96)]
    public int FontSize { get; set; } = 28;
    [Required, StringLength(40)]
    public string FontFamily { get; set; } = "Tajawal";
    [Required, StringLength(7)]
    public string FontColor { get; set; } = "#172033";
    [Required, StringLength(20)]
    public string FontWeight { get; set; } = "600";
    [Required, StringLength(10)]
    public string TextAlign { get; set; } = "center";
    public bool IsVisible { get; set; } = true;
    [Range(-100, 100)]
    public int ZIndex { get; set; } = 1;
    [Range(-180, 180)]
    public int Rotation { get; set; }
    public int SortOrder { get; set; }
}
