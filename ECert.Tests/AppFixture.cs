using ECert.Services;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Boots one real application instance (with a fresh MySQL database) for every test in the
/// collection, so the role/permission flow is exercised end to end without paying startup cost
/// per test.
/// </summary>
public sealed class AppFixture : IAsyncLifetime
{
    public TestAppFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Factory = new TestAppFactory();
        // Touching the server boots the host, which creates the schema and runs the seeder.
        _ = Factory.Services;
        await Factory.EnsureAdminPasswordAsync();
    }

    public async Task DisposeAsync() => await Factory.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class AppCollection : ICollectionFixture<AppFixture>
{
    public const string Name = "app";
}

/// <summary>
/// The admin sections a signed-in user can reach, keyed by the permission(s) that unlock them.
/// This mirrors the navigation and is the contract the tests assert against.
/// </summary>
public static class Sections
{
    public const string ManageRegistrations = AdminPermissions.ManageRegistrations;
    public const string ManageCourses = AdminPermissions.ManageCourses;
    public const string ManageInstructors = AdminPermissions.ManageInstructors;
    public const string ManageCategories = AdminPermissions.ManageCategories;
    public const string IssueCertificates = AdminPermissions.IssueCertificates;
    public const string ManageCertificateDesigns = AdminPermissions.ManageCertificateDesigns;
    public const string ManageFinance = AdminPermissions.ManageFinance;
    public const string ViewInvoices = AdminPermissions.ViewInvoices;
    public const string ManagePayments = AdminPermissions.ManagePayments;
    public const string ViewReports = AdminPermissions.ViewReports;
    public const string ManageCashbox = AdminPermissions.ManageCashbox;
    public const string ManagePosts = AdminPermissions.ManagePosts;
    public const string ManageNews = AdminPermissions.ManageNews;
    public const string ManageHomepage = AdminPermissions.ManageHomepage;
    public const string ManageUsers = AdminPermissions.ManageUsers;
    public const string ManageRoles = AdminPermissions.ManageRoles;
    public const string ViewAuditLog = AdminPermissions.ViewAuditLog;
    public const string ManageAcademicCatalog = AdminPermissions.ManageAcademicCatalog;
    public const string ManagePhoneCountries = AdminPermissions.ManagePhoneCountries;

    public sealed record Section(string Name, string Path, string[] Permissions);

    public static readonly Section[] All =
    {
        new("Registrations", "/Registrations", new[] { ManageRegistrations }),
        new("CoursesManage", "/CoursesManage", new[] { ManageCourses }),
        new("CategoriesManage", "/CategoriesManage", new[] { ManageCategories }),
        new("Instructors", "/Instructors", new[] { ManageInstructors }),
        new("Certificates", "/Certificates", new[] { IssueCertificates }),
        new("CertificateDesigns", "/CertificateDesigns", new[] { ManageCertificateDesigns }),
        new("Invoices", "/Invoices", new[] { ViewInvoices, ManageFinance }),
        new("Payments", "/Payments", new[] { ManagePayments }),
        new("Cashbox", "/Cashbox", new[] { ManageCashbox }),
        new("Reports", "/Reports/CourseRevenue", new[] { ViewReports }),
        new("PostsManage", "/PostsManage", new[] { ManagePosts }),
        new("NewsManage", "/NewsManage", new[] { ManageNews }),
        new("HomepageManage", "/HomepageManage", new[] { ManageHomepage }),
        new("Users", "/Users", new[] { ManageUsers }),
        new("Roles", "/Roles", new[] { ManageRoles }),
        new("AuditLog", "/AuditLog", new[] { ViewAuditLog }),
        new("AcademicCatalog", "/AcademicCatalog", new[] { ManageAcademicCatalog }),
        new("PhoneCountries", "/PhoneCountries", new[] { ManagePhoneCountries }),
    };

    /// <summary>Sections the role editor is allowed to grant (excludes SuperAdmin-only permissions).</summary>
    public static IEnumerable<Section> Grantable => All.Where(section => !section.Permissions.All(AdminPermissions.SuperAdminOnly.Contains));

    public static IEnumerable<Section> ExpectedFor(IEnumerable<string> grantedPermissions)
    {
        var granted = grantedPermissions.ToHashSet(StringComparer.Ordinal);
        return All.Where(section => section.Permissions.Any(granted.Contains));
    }
}
