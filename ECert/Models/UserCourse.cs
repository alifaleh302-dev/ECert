using System.ComponentModel.DataAnnotations;

namespace ECert.Models;

/// <summary>
/// Grants a user administrative authority over a single course: its registrations and the
/// collection of its fees. A user with no rows is <em>unrestricted</em> — SuperAdmin and roles
/// whose duties are not tied to a course keep seeing every course. As soon as at least one row
/// exists the user is confined to exactly those courses, and every list, detail page, export and
/// action outside them is denied.
/// </summary>
public class UserCourse
{
    [Key]
    public int UserCourseId { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }

    public int CourseId { get; set; }
    public Course? Course { get; set; }
}
