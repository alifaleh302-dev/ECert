namespace ECert.Models;

/// <summary>
/// Binds one row of the course form's "supporting organisations" repeater. Uploaded artwork is
/// bound positionally (same index as the row), so a rename never attaches a file to the wrong
/// organisation.
/// </summary>
public class CoursePartnerFormRow
{
    public string? Name { get; set; }
    public IFormFile? Logo { get; set; }
    public IFormFile? Signature { get; set; }

    /// <summary>Set by the "remove saved signature" checkbox; uploads win when both are present.</summary>
    public bool RemoveSignature { get; set; }
}
