using System.Security.Claims;
using ECert.Models;

namespace ECert.Services;

/// <summary>
/// Queryable filters that apply the signed-in user's course confinement to the finance and
/// registration listings. Each helper is a no-op for an unrestricted user (no scope claims), so
/// controllers can apply them unconditionally and the intent stays readable.
/// </summary>
public static class CourseScopeQueries
{
    public static IQueryable<Course> InScope(this IQueryable<Course> query, ClaimsPrincipal user) =>
        user.AllowedCourseIds() is { } ids ? query.Where(c => ids.Contains(c.CourseId)) : query;

    public static IQueryable<Registration> InScope(this IQueryable<Registration> query, ClaimsPrincipal user) =>
        user.AllowedCourseIds() is { } ids ? query.Where(r => ids.Contains(r.CourseId)) : query;

    public static IQueryable<Invoice> InScope(this IQueryable<Invoice> query, ClaimsPrincipal user) =>
        user.AllowedCourseIds() is { } ids
            ? query.Where(i => i.Registration != null && ids.Contains(i.Registration.CourseId))
            : query;

    public static IQueryable<Payment> InScope(this IQueryable<Payment> query, ClaimsPrincipal user) =>
        user.AllowedCourseIds() is { } ids
            ? query.Where(p => p.Invoice != null && p.Invoice.Registration != null && ids.Contains(p.Invoice.Registration.CourseId))
            : query;

    public static IQueryable<Certificate> InScope(this IQueryable<Certificate> query, ClaimsPrincipal user) =>
        user.AllowedCourseIds() is { } ids
            ? query.Where(c => c.Registration != null && ids.Contains(c.Registration.CourseId))
            : query;
}
