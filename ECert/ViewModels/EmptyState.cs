namespace ECert.ViewModels;

/// <summary>
/// Presentation model for the <c>_EmptyState</c> partial.
/// </summary>
public class EmptyState
{
    public string Icon { get; set; } = "bi-inbox";
    public string Title { get; set; } = "لا توجد بيانات";
    public string? Text { get; set; }
    public string? ActionText { get; set; }
    public string? ActionHref { get; set; }
}
