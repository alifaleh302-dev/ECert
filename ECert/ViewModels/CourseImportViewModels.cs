using Microsoft.AspNetCore.Http;

namespace ECert.ViewModels;

public sealed class CourseImportPageViewModel
{
    public IFormFile? File { get; set; }
    public CourseImportPreview? Preview { get; set; }
}

public sealed class CourseImportPreview
{
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public List<CourseImportRow> Rows { get; set; } = new();
    public bool CanCommit => ValidRows > 0 && InvalidRows == 0;
}

public sealed class CourseImportRow
{
    public int RowNumber { get; set; }
    public string CourseName { get; set; } = string.Empty;
    public string? CourseNameArabic { get; set; }
    public string? CourseNameEnglish { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Instructors { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public decimal DiscountValue { get; set; }
    public string? DiscountType { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string? Location { get; set; }
    public string Status { get; set; } = "Draft";
    public bool IsFeatured { get; set; }
    public bool RequiresAcademicDetails { get; set; }
    public string? ShortDescription { get; set; }
    public string? FullDescription { get; set; }
    public string? Objectives { get; set; }
    public string? Content { get; set; }
    public int? CategoryId { get; set; }
    public List<int> InstructorIds { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public bool IsValid => Errors.Count == 0;
}
