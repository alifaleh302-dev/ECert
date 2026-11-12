using System.Security.Claims;
using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Course-level confinement for administrative users. A user is <em>unrestricted</em> when they
/// hold no course assignment (SuperAdmin, or a role whose duties are not tied to a course): the
/// scope claims are absent and every course is visible. Once an administrator assigns courses —
/// directly, or through a category — the login carries one <see cref="ClaimType"/> claim per
/// effective course and the user may only see and act on those courses; anything else is denied,
/// both in listings and in detail/action paths.
/// </summary>
public static class CourseScope
{
    /// <summary>One claim per assigned course id; absent entirely when the user is unrestricted.</summary>
    public const string ClaimType = "CourseScope";

    /// <summary>
    /// Present on a confined user even when their effective course set is empty. Without it, a
    /// user granted a category that currently holds no courses would carry no scope claim and
    /// would be read as <em>unrestricted</em> — a privilege widening. The marker keeps "granted
    /// nothing yet" distinct from "granted everything".
    /// </summary>
    public const string RestrictedMarkerClaimType = "CourseScopeRestricted";

    /// <summary>
    /// The courses the user may administer, or <c>null</c> when the user is unrestricted. The
    /// distinction matters: <c>null</c> means "no filter". A confined user with no effective
    /// courses returns an empty list, which the query filters turn into "matches nothing".
    /// </summary>
    public static IReadOnlyCollection<int>? AllowedCourseIds(this ClaimsPrincipal user)
    {
        if (user.IsSuperAdmin()) return null;

        var ids = user.FindAll(ClaimType)
            .Select(claim => int.TryParse(claim.Value, out var id) ? id : (int?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();

        if (ids.Length == 0 && !user.HasClaim(claim => claim.Type == RestrictedMarkerClaimType))
            return null;

        return ids;
    }

    /// <summary>True when the user is confined to a specific set of courses.</summary>
    public static bool IsCourseRestricted(this ClaimsPrincipal user) => user.AllowedCourseIds() is not null;

    /// <summary>True when the user may administer the given course (or is unrestricted).</summary>
    public static bool CanAccessCourse(this ClaimsPrincipal user, int courseId) =>
        user.AllowedCourseIds() is not { } ids || ids.Contains(courseId);

    /// <summary>
    /// True when the user may administer the given course id, treating a missing id (an entity whose
    /// course could not be resolved) as inaccessible for a confined user.
    /// </summary>
    public static bool CanAccessCourse(this ClaimsPrincipal user, int? courseId) =>
        user.AllowedCourseIds() is not { } ids || (courseId.HasValue && ids.Contains(courseId.Value));

    /// <summary>
    /// True when the user may administer the course the registration belongs to. Used by every
    /// registration-scoped action so a crafted id cannot reach a course outside the scope.
    /// </summary>
    public static bool CanAccessCourseOf(this ClaimsPrincipal user, Registration registration) =>
        user.CanAccessCourse(registration.CourseId);

    /// <summary>
    /// Expands a user's assignments — the courses named directly plus every course inside the
    /// categories they were granted — into the concrete set of course ids that becomes their
    /// scope claims. Returns an empty set when the user has no assignment at all, which is the
    /// signal for <em>unrestricted</em>. Category membership is resolved at sign-in, so a course
    /// added to a granted category afterwards is picked up at the user's next login.
    /// </summary>
    public static async Task<IReadOnlyList<int>> ResolveEffectiveCourseIdsAsync(
        ECertDbContext db, int userId, CancellationToken cancellationToken = default)
    {
        var directCourseIds = await db.UserCourses
            .Where(uc => uc.UserId == userId)
            .Select(uc => uc.CourseId)
            .ToListAsync(cancellationToken);

        var grantedCategoryIds = await db.UserCategories
            .Where(uc => uc.UserId == userId)
            .Select(uc => uc.CategoryId)
            .ToListAsync(cancellationToken);

        var categoryCourseIds = grantedCategoryIds.Count == 0
            ? new List<int>()
            : await db.Courses
                .Where(c => grantedCategoryIds.Contains(c.CategoryId))
                .Select(c => c.CourseId)
                .ToListAsync(cancellationToken);

        return directCourseIds.Concat(categoryCourseIds).Distinct().ToArray();
    }

    /// <summary>
    /// Adds the course-scope claims for a signing-in user. A SuperAdmin, and any user with no
    /// assignment at all, stays unrestricted (no claims added). A user who has assignments but
    /// whose categories currently contain no courses gets the restriction marker instead, so they
    /// are confined to nothing rather than silently promoted to everything.
    /// </summary>
    public static async Task AddScopeClaimsAsync(
        List<Claim> claims, ECertDbContext db, User user, bool isSuperAdmin,
        CancellationToken cancellationToken = default)
    {
        if (isSuperAdmin) return;

        var hasAssignment = await db.UserCourses.AnyAsync(uc => uc.UserId == user.UserId, cancellationToken)
            || await db.UserCategories.AnyAsync(uc => uc.UserId == user.UserId, cancellationToken);
        if (!hasAssignment) return;

        claims.Add(new Claim(RestrictedMarkerClaimType, "true"));

        var effectiveCourseIds = await ResolveEffectiveCourseIdsAsync(db, user.UserId, cancellationToken);
        foreach (var courseId in effectiveCourseIds)
            claims.Add(new Claim(ClaimType, courseId.ToString()));
    }
}
