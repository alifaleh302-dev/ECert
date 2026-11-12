namespace ECert.Models.ViewModels;

/// <summary>
/// Data for the course-scope picker partial: the categories and courses a SuperAdmin can grant,
/// together with what is currently granted. Shared by the user create and edit screens so the two
/// stay identical.
/// </summary>
public class CourseScopePickerViewModel
{
    public IReadOnlyList<Category> Categories { get; init; } = new List<Category>();
    public IReadOnlyList<Course> Courses { get; init; } = new List<Course>();
    public IReadOnlyCollection<int> SelectedCategoryIds { get; init; } = new List<int>();
    public IReadOnlyCollection<int> SelectedCourseIds { get; init; } = new List<int>();

    /// <summary>
    /// When false the picker is replaced by an explanatory note — a non-SuperAdmin administrator
    /// never edits another user's scope (their accounts inherit the creator's scope instead).
    /// </summary>
    public bool CanAssign { get; init; }

    /// <summary>Set when the account being edited is the SuperAdmin itself (scope cannot be limited).</summary>
    public bool IsUnscopableAccount { get; init; }
}
