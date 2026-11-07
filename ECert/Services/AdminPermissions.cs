using System.Security.Claims;

namespace ECert.Services;

/// <summary>
/// Single source of truth for the permission catalog and for the mapping between an
/// admin section (controller) and the permission that unlocks it. The seeder, the
/// sidebar navigation and the per-controller guards all read from here, so a permission
/// can never be offered in the role editor without a section that honours it — and a
/// section can never be reachable without its permission.
/// </summary>
public static class AdminPermissions
{
    public const string ManageRegistrations = "manage-registrations";
    public const string ManageCourses = "manage-courses";
    public const string ManageInstructors = "manage-instructors";
    public const string ManageCategories = "manage-categories";
    public const string IssueCertificates = "issue-certificates";
    public const string ManageCertificateDesigns = "manage-certificate-designs";
    public const string ManageFinance = "manage-finance";
    public const string ViewInvoices = "view-invoices";
    public const string ManagePayments = "manage-payments";
    public const string ViewReports = "view-reports";
    public const string ManageCashbox = "manage-cashbox";
    public const string WithdrawCashbox = "withdraw-cashbox";
    public const string ManagePosts = "manage-posts";
    public const string ManageNews = "manage-news";
    public const string ManageHomepage = "manage-homepage";
    public const string ManageUsers = "manage-users";
    public const string ManageRoles = "manage-roles";
    public const string ViewAuditLog = "view-audit-log";
    public const string ManageAcademicCatalog = "manage-academic-catalog";
    public const string ManagePhoneCountries = "manage-phone-countries";

    public sealed record PermissionDefinition(string Name, string Description, string Group);

    /// <summary>
    /// Every permission the system understands. The seeder keeps the Permissions table in
    /// sync with this list and the role editor renders it grouped by <see cref="PermissionDefinition.Group"/>.
    /// </summary>
    public static readonly IReadOnlyList<PermissionDefinition> Catalog = new List<PermissionDefinition>
    {
        new(ManageRegistrations, "إدارة التسجيلات", "إدارة التدريب"),
        new(ManageCourses, "إدارة الدورات", "إدارة التدريب"),
        new(ManageInstructors, "إدارة المدربين", "إدارة التدريب"),
        new(ManageCategories, "إدارة الفئات", "إدارة التدريب"),
        new(IssueCertificates, "إصدار الشهادات", "الشهادات"),
        new(ManageCertificateDesigns, "إدارة تصميم الشهادات", "الشهادات"),
        new(ManageFinance, "إدارة المالية", "الإدارة المالية"),
        new(ViewInvoices, "مشاهدة الفواتير", "الإدارة المالية"),
        new(ManagePayments, "إدارة الدفعات", "الإدارة المالية"),
        new(ViewReports, "مشاهدة تقارير الإيرادات", "الإدارة المالية"),
        new(ManageCashbox, "ترحيل أرصدة الدورات إلى الصندوق", "الإدارة المالية"),
        new(WithdrawCashbox, "سحب من الصندوق العام", "الإدارة المالية"),
        new(ManagePosts, "إدارة المنشورات", "المحتوى"),
        new(ManageNews, "إدارة الأخبار", "المحتوى"),
        new(ManageHomepage, "إدارة الصفحة الرئيسية", "المحتوى"),
        new(ManageUsers, "إدارة المستخدمين", "النظام"),
        new(ManageRoles, "إدارة الأدوار والصلاحيات", "النظام"),
        new(ViewAuditLog, "مشاهدة سجل العمليات", "النظام"),
        new(ManageAcademicCatalog, "إدارة البيانات الجامعية", "النظام"),
        new(ManagePhoneCountries, "إدارة مفاتيح الهواتف", "النظام"),
    };

    /// <summary>
    /// The permissions that unlock each admin section, keyed by controller name. A section is
    /// reachable when the role holds <em>any</em> of its permissions — several permissions can
    /// legitimately open the same page (for example the finance umbrella <see cref="ManageFinance"/>
    /// and the read-only <see cref="ViewInvoices"/> both open Invoices). The sidebar, the dashboard
    /// cards and the controller guards all consult this map, so a granted permission is never
    /// silently ignored: every permission in <see cref="Catalog"/> appears in at least one entry here.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> SectionAccess =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Registrations"] = new[] { ManageRegistrations },
            ["CoursesManage"] = new[] { ManageCourses },
            ["CategoriesManage"] = new[] { ManageCategories },
            ["Instructors"] = new[] { ManageInstructors },
            ["Certificates"] = new[] { IssueCertificates },
            ["CertificateDesigns"] = new[] { ManageCertificateDesigns },
            ["Invoices"] = new[] { ViewInvoices, ManageFinance },
            ["Payments"] = new[] { ManagePayments },
            ["Cashbox"] = new[] { ManageCashbox, WithdrawCashbox },
            ["Reports"] = new[] { ViewReports },
            ["PostsManage"] = new[] { ManagePosts },
            ["NewsManage"] = new[] { ManageNews },
            ["HomepageManage"] = new[] { ManageHomepage },
            ["Users"] = new[] { ManageUsers },
            ["Roles"] = new[] { ManageRoles },
            ["AuditLog"] = new[] { ViewAuditLog },
            ["AcademicCatalog"] = new[] { ManageAcademicCatalog },
            ["PhoneCountries"] = new[] { ManagePhoneCountries },
        };

    /// <summary>
    /// The section (controller) names a permission opens, in navigation order. Used to explain to
    /// an administrator, while editing a role, exactly what each permission grants access to.
    /// </summary>
    public static IEnumerable<string> SectionsFor(string permission) =>
        SectionAccess.Where(entry => entry.Value.Contains(permission, StringComparer.Ordinal))
            .Select(entry => entry.Key);

    /// <summary>
    /// Permissions that must never be handed to a custom role: they govern who may change
    /// roles and users, so granting them would let a role escalate its own privileges.
    /// Only the built-in SuperAdmin holds them.
    /// </summary>
    public static readonly IReadOnlySet<string> SuperAdminOnly =
        new HashSet<string>(StringComparer.Ordinal) { ManageRoles };
}

public static class AdminPrincipalExtensions
{
    public const string PermissionClaimType = "Permission";
    public const string SuperAdminRole = "SuperAdmin";

    public static bool IsSuperAdmin(this ClaimsPrincipal user) => user.IsInRole(SuperAdminRole);

    public static bool HasPermission(this ClaimsPrincipal user, string permission) =>
        user.HasClaim(c => c.Type == PermissionClaimType && c.Value == permission);

    public static bool HasAnyPermission(this ClaimsPrincipal user, params string[] permissions) =>
        permissions.Any(user.HasPermission);

    /// <summary>
    /// True when the user may open the admin section owned by <paramref name="controller"/>.
    /// SuperAdmin passes everything; otherwise the section is unlocked by any of the permissions
    /// mapped to it in <see cref="AdminPermissions.SectionAccess"/>. A controller with no mapping
    /// is treated as SuperAdmin-only, so a new page is never accidentally public.
    /// </summary>
    public static bool CanAccessSection(this ClaimsPrincipal user, string controller)
    {
        if (user.IsSuperAdmin()) return true;
        if (!AdminPermissions.SectionAccess.TryGetValue(controller, out var permissions)) return false;
        return permissions.Any(user.HasPermission);
    }

    /// <summary>
    /// True when the user may open the admin area at all — the dashboard and sidebar are
    /// available exactly to those holding at least one admin permission.
    /// </summary>
    public static bool HasAnyAdminPermission(this ClaimsPrincipal user) =>
        user.IsSuperAdmin() || AdminPermissions.Catalog.Any(definition => user.HasPermission(definition.Name));
}
