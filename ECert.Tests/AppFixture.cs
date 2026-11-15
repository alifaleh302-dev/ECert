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
    public const string ViewRegistrations = AdminPermissions.ViewRegistrations;
    public const string ManageCourses = AdminPermissions.ManageCourses;
    public const string ViewCourses = AdminPermissions.ViewCourses;
    public const string ManageInstructors = AdminPermissions.ManageInstructors;
    public const string ViewInstructors = AdminPermissions.ViewInstructors;
    public const string ManageCategories = AdminPermissions.ManageCategories;
    public const string ViewCategories = AdminPermissions.ViewCategories;
    public const string IssueCertificates = AdminPermissions.IssueCertificates;
    public const string ViewCertificates = AdminPermissions.ViewCertificates;
    public const string ManageCertificateDesigns = AdminPermissions.ManageCertificateDesigns;
    public const string ManageFinance = AdminPermissions.ManageFinance;
    public const string ViewInvoices = AdminPermissions.ViewInvoices;
    public const string ManagePayments = AdminPermissions.ManagePayments;
    public const string ViewPayments = AdminPermissions.ViewPayments;
    public const string ViewReports = AdminPermissions.ViewReports;
    public const string ViewCustody = AdminPermissions.ViewCustody;
    public const string ApproveCustody = AdminPermissions.ApproveCustody;
    public const string ViewCashbox = AdminPermissions.ViewCashbox;
    public const string ManageCashbox = AdminPermissions.ManageCashbox;
    public const string ManageRefunds = AdminPermissions.ManageRefunds;
    public const string ApproveRefunds = AdminPermissions.ApproveRefunds;
    public const string ViewReceivables = AdminPermissions.ViewReceivables;
    public const string ViewFinancialAudit = AdminPermissions.ViewFinancialAudit;
    public const string ManageExpenses = AdminPermissions.ManageExpenses;
    public const string ApproveExpenses = AdminPermissions.ApproveExpenses;
    public const string ViewExpenses = AdminPermissions.ViewExpenses;
    public const string ManageInstallments = AdminPermissions.ManageInstallments;
    public const string ViewInstallments = AdminPermissions.ViewInstallments;
    public const string ViewFinanceDashboard = AdminPermissions.ViewFinanceDashboard;
    public const string ManageFinanceSettings = AdminPermissions.ManageFinanceSettings;
    public const string ManagePosts = AdminPermissions.ManagePosts;
    public const string ManageNews = AdminPermissions.ManageNews;
    public const string ManageHomepage = AdminPermissions.ManageHomepage;
    public const string ManageUsers = AdminPermissions.ManageUsers;
    public const string ManageRoles = AdminPermissions.ManageRoles;
    public const string ViewAuditLog = AdminPermissions.ViewAuditLog;
    public const string ManageAcademicCatalog = AdminPermissions.ManageAcademicCatalog;
    public const string ManagePhoneCountries = AdminPermissions.ManagePhoneCountries;
    public const string ManageSecuritySettings = AdminPermissions.ManageSecuritySettings;
    public const string ManageCertificateExportSettings = AdminPermissions.ManageCertificateExportSettings;
    public const string ViewSecurityAudit = AdminPermissions.ViewSecurityAudit;

    public sealed record Section(string Name, string Path, string[] Permissions, bool SuperAdminOnly = false);

    public static readonly Section[] All =
    {
        new("Registrations", "/Registrations", new[] { ManageRegistrations, ViewRegistrations }),
        new("CoursesManage", "/CoursesManage", new[] { ManageCourses, ViewCourses }),
        new("CategoriesManage", "/CategoriesManage", new[] { ManageCategories, ViewCategories }),
        new("Instructors", "/Instructors", new[] { ManageInstructors, ViewInstructors }),
        new("Certificates", "/Certificates", new[] { IssueCertificates, ViewCertificates }),
        new("CertificateDesigns", "/CertificateDesigns", new[] { ManageCertificateDesigns }),
        new("Invoices", "/Invoices", new[] { ViewInvoices, ManageFinance }),
        new("Payments", "/Payments", new[] { ManagePayments, ViewPayments }),
        new("Cashbox", "/Cashbox", new[] { ViewCashbox, ManageCashbox }),
        new("Custody", "/Custody", new[] { ViewCustody, ApproveCustody }),
        new("Refunds", "/Refunds", new[] { ManageRefunds, ApproveRefunds }),
        new("Receivables", "/Receivables", new[] { ViewReceivables }),
        new("FinancialAudit", "/FinancialAudit", new[] { ViewFinancialAudit }),
        new("Expenses", "/Expenses", new[] { ManageExpenses, ViewExpenses, ApproveExpenses }),
        new("Installments", "/Installments", new[] { ManageInstallments, ViewInstallments }),
        new("Finance", "/Finance", new[] { ViewFinanceDashboard }),
        new("FinanceSettings", "/FinanceSettings", new[] { ManageFinanceSettings }),
        new("Reports", "/Reports/CourseRevenue", new[] { ViewReports }),
        new("SecuritySettings", "/SecuritySettings", new[] { ManageSecuritySettings }, SuperAdminOnly: true),
        new("CertificateExportSettings", "/CertificateExportSettings", new[] { ManageCertificateExportSettings }, SuperAdminOnly: true),
        new("PostsManage", "/PostsManage", new[] { ManagePosts }),
        new("NewsManage", "/NewsManage", new[] { ManageNews }),
        new("HomepageManage", "/HomepageManage", new[] { ManageHomepage }),
        new("Users", "/Users", new[] { ManageUsers }),
        new("Roles", "/Roles", new[] { ManageRoles }),
        new("AuditLog", "/AuditLog", new[] { ViewAuditLog, ViewSecurityAudit }),
        new("AcademicCatalog", "/AcademicCatalog", new[] { ManageAcademicCatalog }),
        new("PhoneCountries", "/PhoneCountries", new[] { ManagePhoneCountries }),
    };

    /// <summary>Sections the role editor is allowed to grant (excludes SuperAdmin-only sections).</summary>
    public static IEnumerable<Section> Grantable => All.Where(section => !section.SuperAdminOnly);

    /// <summary>
    /// Every section must list exactly the permissions that unlock it in <c>AdminPermissions</c>,
    /// so the test fixture can never drift from the production section map.
    /// </summary>
    public static IEnumerable<string> DriftedSections()
    {
        foreach (var section in All.Where(s => !s.SuperAdminOnly))
        {
            var expected = section.Permissions.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var actual = AdminPermissions.SectionAccess.TryGetValue(section.Name, out var mapped)
                ? mapped.OrderBy(p => p, StringComparer.Ordinal).ToArray()
                : Array.Empty<string>();
            if (!expected.SequenceEqual(actual))
                yield return section.Name;
        }
    }

    public static IEnumerable<Section> ExpectedFor(IEnumerable<string> grantedPermissions)
    {
        var granted = grantedPermissions.ToHashSet(StringComparer.Ordinal);
        return All.Where(section => !section.SuperAdminOnly && section.Permissions.Any(granted.Contains));
    }
}
