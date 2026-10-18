using Microsoft.AspNetCore.Http;

namespace ECert.ViewModels;

public sealed class RegistrationImportPageViewModel
{
    public IFormFile? File { get; set; }
    public int? CourseId { get; set; }
    public string ImportStatus { get; set; } = "Pending";
    public bool CourseRequiresAcademicDetails { get; set; }
    public List<RegistrationImportCourseOption> Courses { get; set; } = new();
    public RegistrationImportPreview? Preview { get; set; }
}

public sealed class RegistrationImportCourseOption
{
    public int CourseId { get; set; }
    public string CourseName { get; set; } = string.Empty;
    public bool RequiresAcademicDetails { get; set; }
}

public sealed class RegistrationImportPreview
{
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public int CourseId { get; set; }
    public string CourseName { get; set; } = string.Empty;
    public string ImportStatus { get; set; } = "Pending";
    public bool CourseRequiresAcademicDetails { get; set; }
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public List<RegistrationImportRow> Rows { get; set; } = new();
    public bool CanCommit => ValidRows > 0 && InvalidRows == 0;
}

public sealed class RegistrationImportRow
{
    public int RowNumber { get; set; }
    public string FullNameArabic { get; set; } = string.Empty;
    public string FullNameEnglish { get; set; } = string.Empty;
    public string? Gender { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string? Country { get; set; }
    public string? NormalizedPhone { get; set; }
    public string? Email { get; set; }
    public string? HeardFrom { get; set; }
    public string? University { get; set; }
    public string? College { get; set; }
    public string? Specialization { get; set; }
    public string? AcademicLevel { get; set; }
    public int? UniversityId { get; set; }
    public int? CollegeId { get; set; }
    public int? AcademicSpecializationId { get; set; }
    public List<string> Errors { get; set; } = new();
    public bool IsValid => Errors.Count == 0;
}
