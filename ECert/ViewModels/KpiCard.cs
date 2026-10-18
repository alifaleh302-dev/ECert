namespace ECert.ViewModels;

/// <summary>
/// Presentation model for a dashboard KPI tile rendered by <c>_KpiCard</c>.
/// </summary>
public class KpiCard
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Icon { get; set; } = "bi-graph-up";

    /// <summary>One of: brand, success, warning, danger, info.</summary>
    public string Tone { get; set; } = "brand";

    public string? Hint { get; set; }

    /// <summary>When set the tile renders as a link.</summary>
    public string? Href { get; set; }
}
