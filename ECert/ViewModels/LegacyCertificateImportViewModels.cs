using Microsoft.AspNetCore.Http;

namespace ECert.ViewModels;

/// <summary>Form shown before the legacy file is uploaded: the values used as defaults for every row.</summary>
public sealed class LegacyCertificateImportPageViewModel
{
    public IFormFile? File { get; set; }

    public int? DefaultInstructorId { get; set; }
    public int? DefaultCategoryId { get; set; }

    public List<LegacyImportOption> Instructors { get; set; } = new();
    public List<LegacyImportOption> Categories { get; set; } = new();

    public LegacyCertificateImportPreview? Preview { get; set; }
}

public sealed class LegacyImportOption
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Parsed, validated snapshot of an uploaded legacy file. Held in <c>ExcelPreviewStore</c> between
/// the upload request and the confirm request so the user can review and edit before committing.
/// </summary>
public sealed class LegacyCertificateImportPreview
{
    public string Token { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>True when the workbook stored any formula; its stored result was used.</summary>
    public bool HasFormulas { get; set; }

    /// <summary>One entry per distinct course name found in the file, each with its own category/instructors.</summary>
    public List<LegacyImportCourseGroup> Courses { get; set; } = new();

    public List<LegacyCertificateImportRow> Rows { get; set; } = new();

    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int DuplicateRows { get; set; }
    public int InvalidRows { get; set; }
    public int NewCourseCount => Courses.Count(c => c.Action == LegacyCourseAction.Create && c.IsConfigured);
    public int LinkedCourseCount => Courses.Count(c => c.Action == LegacyCourseAction.Link && c.IsConfigured);

    /// <summary>Every course that has valid rows is configured, and at least one row is valid.</summary>
    public bool CanCommit => ValidRows > 0 && Courses.All(c => !c.HasValidRows || c.IsConfigured);
}

/// <summary>Whether a course found in the file should be created or linked to an existing one.</summary>
public enum LegacyCourseAction
{
    Create = 0,
    Link = 1
}

/// <summary>
/// A distinct course name from the file plus the choices the administrator makes for it before the
/// import: its category, its instructors (one or more) and whether to create it or link an existing
/// course of the same name.
/// </summary>
public sealed class LegacyImportCourseGroup
{
    /// <summary>Stable key used to bind rows to their group (the normalised course name).</summary>
    public string Key { get; set; } = string.Empty;

    public string NameArabic { get; set; } = string.Empty;
    public string NameEnglish { get; set; } = string.Empty;

    public int? CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;

    public List<int> InstructorIds { get; set; } = new();
    public List<string> InstructorNames { get; set; } = new();

    public LegacyCourseAction Action { get; set; } = LegacyCourseAction.Create;

    /// <summary>Set when <see cref="Action"/> is <see cref="LegacyCourseAction.Link"/>.</summary>
    public int? LinkedCourseId { get; set; }
    public string LinkedCourseLabel { get; set; } = string.Empty;

    /// <summary>Courses in the system whose name matches this group, offered as link targets.</summary>
    public List<LegacyImportOption> MatchingCourses { get; set; } = new();

    /// <summary>True when at least one valid row belongs to this group; only such groups block the commit.</summary>
    public bool HasValidRows { get; set; }

    public bool IsConfigured
    {
        get
        {
            if (CategoryId is null || InstructorIds.Count == 0) return false;
            if (Action == LegacyCourseAction.Link && LinkedCourseId is null) return false;
            return true;
        }
    }
}

public sealed class LegacyCertificateImportRow
{
    public int RowNumber { get; set; }

    /// <summary>Key of the <see cref="LegacyImportCourseGroup"/> this row belongs to.</summary>
    public string CourseKey { get; set; } = string.Empty;

    public string FullNameArabic { get; set; } = string.Empty;
    public string FullNameEnglish { get; set; } = string.Empty;
    public string CertificateNumber { get; set; } = string.Empty;
    public string CourseName { get; set; } = string.Empty;
    public DateTime? IssueDate { get; set; }

    public bool IsValid { get; set; }

    /// <summary>True when the certificate number already exists (in the file or the database); the row is skipped on commit.</summary>
    public bool IsDuplicate { get; set; }

    public List<string> Errors { get; set; } = new();
}

/// <summary>Outcome of a committed import, shown as the post-import report.</summary>
public sealed class LegacyCertificateImportResult
{
    public int Imported { get; set; }
    public int Skipped { get; set; }
    public int CoursesCreated { get; set; }
    public int CoursesLinked { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.Now;

    public List<LegacyCertificateImportResultRow> Rows { get; set; } = new();
}

public sealed class LegacyCertificateImportResultRow
{
    public string CertificateNumber { get; set; } = string.Empty;
    public string TraineeNameArabic { get; set; } = string.Empty;
    public string TraineeNameEnglish { get; set; } = string.Empty;
    public string CourseName { get; set; } = string.Empty;
    public string PublicId { get; set; } = string.Empty;
    public string VerificationCode { get; set; } = string.Empty;
    public string VerificationUrl { get; set; } = string.Empty;
    public string QrDataUri { get; set; } = string.Empty;
    public bool Imported { get; set; }
    public string? SkipReason { get; set; }
}

