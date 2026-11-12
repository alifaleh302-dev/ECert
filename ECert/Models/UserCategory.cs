using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

/// <summary>
/// Grants a user administrative authority over every course in a category — including courses
/// added to that category later. This is the coarser of the two scoping mechanisms: a category
/// grant is for "this collector handles all health courses", while <see cref="UserCourse"/> is
/// for naming individual courses. A user with no rows of either kind is <em>unrestricted</em>;
/// the moment one row exists (of either kind) the user is confined to the union of the named
/// courses and the courses inside the named categories.
/// </summary>
public class UserCategory
{
    [Key]
    public int UserCategoryId { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int CategoryId { get; set; }
    public Category? Category { get; set; }
}
