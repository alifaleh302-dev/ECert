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
    public const string ViewRegistrations = "view-registrations";
    public const string ManageCourses = "manage-courses";
    public const string ViewCourses = "view-courses";
    public const string ManageInstructors = "manage-instructors";
    public const string ViewInstructors = "view-instructors";
    public const string ManageCategories = "manage-categories";
    public const string ViewCategories = "view-categories";
    public const string IssueCertificates = "issue-certificates";
    public const string ViewCertificates = "view-certificates";
    public const string ManageCertificateDesigns = "manage-certificate-designs";
    public const string ManageFinance = "manage-finance";
    public const string ViewInvoices = "view-invoices";
    public const string ManagePayments = "manage-payments";
    public const string ViewPayments = "view-payments";
    public const string ViewReports = "view-reports";
    public const string ViewCustody = "view-custody";
    public const string ManageRefunds = "manage-refunds";
    public const string ApproveRefunds = "approve-refunds";
    public const string ViewReceivables = "view-receivables";
    public const string ViewFinancialAudit = "view-financial-audit";
    public const string ManageExpenses = "manage-expenses";
    public const string ApproveExpenses = "approve-expenses";
    public const string ViewExpenses = "view-expenses";
    public const string ManageInstallments = "manage-installments";
    public const string ViewInstallments = "view-installments";
    public const string ViewFinanceDashboard = "view-finance-dashboard";
    public const string ManageFinanceSettings = "manage-finance-settings";
    public const string ViewCashbox = "view-cashbox";
    public const string ManageCashbox = "manage-cashbox";
    public const string ApproveCustody = "approve-custody";
    public const string ManagePosts = "manage-posts";
    public const string ManageNews = "manage-news";
    public const string ManageHomepage = "manage-homepage";
    public const string ManageUsers = "manage-users";
    public const string ManageRoles = "manage-roles";
    public const string ViewAuditLog = "view-audit-log";
    public const string ManageAcademicCatalog = "manage-academic-catalog";
    public const string ManagePhoneCountries = "manage-phone-countries";
    public const string ViewSecurityAudit = "view-security-audit";
    public const string ManageSecuritySettings = "manage-security-settings";
    public const string ManageCertificateExportSettings = "manage-certificate-export-settings";

    public sealed record PermissionDefinition(string Name, string Description, string Group);

    /// <summary>
    /// Every permission the system understands. The seeder keeps the Permissions table in
    /// sync with this list and the role editor renders it grouped by <see cref="PermissionDefinition.Group"/>.
    /// </summary>
    public static readonly IReadOnlyList<PermissionDefinition> Catalog = new List<PermissionDefinition>
    {
        new(ManageRegistrations, "إدارة التسجيلات", "إدارة التدريب"),
        new(ViewRegistrations, "مشاهدة التسجيلات (قراءة فقط)", "إدارة التدريب"),
        new(ManageCourses, "إدارة الدورات", "إدارة التدريب"),
        new(ViewCourses, "مشاهدة الدورات (قراءة فقط)", "إدارة التدريب"),
        new(ManageInstructors, "إدارة المدربين", "إدارة التدريب"),
        new(ViewInstructors, "مشاهدة المدربين (قراءة فقط)", "إدارة التدريب"),
        new(ManageCategories, "إدارة الفئات", "إدارة التدريب"),
        new(ViewCategories, "مشاهدة الفئات (قراءة فقط)", "إدارة التدريب"),
        new(IssueCertificates, "إصدار الشهادات", "الشهادات"),
        new(ViewCertificates, "مشاهدة الشهادات (قراءة فقط)", "الشهادات"),
        new(ManageCertificateDesigns, "إدارة تصميم الشهادات", "الشهادات"),
        new(ManageFinance, "إدارة المالية", "الإدارة المالية"),
        new(ViewInvoices, "مشاهدة الفواتير", "الإدارة المالية"),
        new(ManagePayments, "إدارة الدفعات", "الإدارة المالية"),
        new(ViewPayments, "مشاهدة الدفعات (قراءة فقط)", "الإدارة المالية"),
        new(ViewReports, "مشاهدة تقارير الإيرادات", "الإدارة المالية"),
        new(ViewCustody, "عهدة الموظف والتوريد للصندوق", "الإدارة المالية"),
        new(ApproveCustody, "اعتماد توريد العهد إلى الصندوق", "الإدارة المالية"),
        new(ViewCashbox, "مشاهدة الصندوق الرئيسي (قراءة فقط)", "الإدارة المالية"),
        new(ManageCashbox, "إدارة الصندوق الرئيسي", "الإدارة المالية"),
        new(ManageRefunds, "استرداد الرسوم", "الإدارة المالية"),
        new(ApproveRefunds, "اعتماد الاستردادات", "الإدارة المالية"),
        new(ViewReceivables, "تقارير المتأخرات وكشف الحساب", "الإدارة المالية"),
        new(ViewFinancialAudit, "سجل التدقيق المالي", "الإدارة المالية"),
        new(ManageExpenses, "إدارة المصروفات", "الإدارة المالية"),
        new(ApproveExpenses, "اعتماد المصروفات", "الإدارة المالية"),
        new(ViewExpenses, "مشاهدة المصروفات (قراءة فقط)", "الإدارة المالية"),
        new(ManageInstallments, "إدارة الأقساط والخطة الدراسية", "الإدارة المالية"),
        new(ViewInstallments, "مشاهدة الأقساط (قراءة فقط)", "الإدارة المالية"),
        new(ViewFinanceDashboard, "لوحة المؤشرات المالية", "الإدارة المالية"),
        new(ManageFinanceSettings, "إعدادات النظام المالية (العملة والفترات)", "الإدارة المالية"),
        new(ManagePosts, "إدارة المنشورات", "المحتوى"),
        new(ManageNews, "إدارة الأخبار", "المحتوى"),
        new(ManageHomepage, "إدارة الصفحة الرئيسية", "المحتوى"),
        new(ManageUsers, "إدارة المستخدمين", "النظام"),
        new(ManageRoles, "إدارة الأدوار والصلاحيات", "النظام"),
        new(ViewAuditLog, "مشاهدة سجل العمليات", "النظام"),
        new(ManageAcademicCatalog, "إدارة البيانات الجامعية", "النظام"),
        new(ManagePhoneCountries, "إدارة مفاتيح الهواتف", "النظام"),
        new(ViewSecurityAudit, "مراجعة سجلات العمليات والتدقيق (قراءة فقط)", "النظام"),
        new(ManageSecuritySettings, "إعدادات الأمان والنسخ الاحتياطي", "النظام"),
        new(ManageCertificateExportSettings, "إعدادات تصدير الشهادات", "النظام"),
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
            ["Registrations"] = new[] { ManageRegistrations, ViewRegistrations },
            ["CoursesManage"] = new[] { ManageCourses, ViewCourses },
            ["CategoriesManage"] = new[] { ManageCategories, ViewCategories },
            ["Instructors"] = new[] { ManageInstructors, ViewInstructors },
            ["Certificates"] = new[] { IssueCertificates, ViewCertificates },
            ["CertificateDesigns"] = new[] { ManageCertificateDesigns },
            ["Invoices"] = new[] { ViewInvoices, ManageFinance },
            ["Payments"] = new[] { ManagePayments, ViewPayments },
            ["Custody"] = new[] { ViewCustody, ApproveCustody },
            ["Refunds"] = new[] { ManageRefunds, ApproveRefunds },
            ["Receivables"] = new[] { ViewReceivables },
            ["FinancialAudit"] = new[] { ViewFinancialAudit },
            ["Expenses"] = new[] { ManageExpenses, ViewExpenses, ApproveExpenses },
            ["Installments"] = new[] { ManageInstallments, ViewInstallments },
            ["Finance"] = new[] { ViewFinanceDashboard },
            ["FinanceSettings"] = new[] { ManageFinanceSettings },
            ["Cashbox"] = new[] { ViewCashbox, ManageCashbox },
            ["Reports"] = new[] { ViewReports },
            ["PostsManage"] = new[] { ManagePosts },
            ["NewsManage"] = new[] { ManageNews },
            ["HomepageManage"] = new[] { ManageHomepage },
            ["Users"] = new[] { ManageUsers },
            ["Roles"] = new[] { ManageRoles },
            ["AuditLog"] = new[] { ViewAuditLog, ViewSecurityAudit },
            ["AcademicCatalog"] = new[] { ManageAcademicCatalog },
            ["PhoneCountries"] = new[] { ManagePhoneCountries },
            ["SecuritySettings"] = new[] { ManageSecuritySettings },
            ["CertificateExportSettings"] = new[] { ManageCertificateExportSettings },
        };

    /// <summary>
    /// The section (controller) names a permission opens, in navigation order. Used to explain to
    /// an administrator, while editing a role, exactly what each permission grants access to.
    /// </summary>
    public static IEnumerable<string> SectionsFor(string permission) =>
        SectionAccess.Where(entry => entry.Value.Contains(permission, StringComparer.Ordinal))
            .Select(entry => entry.Key);

    /// <summary>
    /// Human-readable Arabic name for each section (controller), mirroring the sidebar so the role
    /// editor can tell an administrator which pages a permission opens without duplicating labels.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SectionLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Registrations"] = "التسجيلات",
            ["CoursesManage"] = "الدورات",
            ["CategoriesManage"] = "الفئات",
            ["Instructors"] = "المدربون",
            ["Certificates"] = "الشهادات",
            ["CertificateDesigns"] = "تصاميم الشهادات",
            ["Finance"] = "لوحة المؤشرات المالية",
            ["Invoices"] = "الفواتير",
            ["Payments"] = "الدفعات",
            ["Installments"] = "الأقساط والخطط",
            ["Custody"] = "عهدة الموظف والتوريد",
            ["Receivables"] = "المتأخرات",
            ["Refunds"] = "استرداد الرسوم",
            ["Expenses"] = "المصروفات",
            ["Cashbox"] = "الصندوق",
            ["Reports"] = "تقارير الإيرادات",
            ["FinancialAudit"] = "سجل التدقيق المالي",
            ["FinanceSettings"] = "إعدادات النظام المالية",
            ["PostsManage"] = "المنشورات",
            ["NewsManage"] = "الأخبار",
            ["HomepageManage"] = "الصفحة الرئيسية",
            ["Users"] = "المستخدمون",
            ["Roles"] = "الأدوار والصلاحيات",
            ["AuditLog"] = "سجل العمليات",
            ["SecuritySettings"] = "الأمان والنسخ الاحتياطي",
            ["CertificateExportSettings"] = "إعدادات تصدير الشهادات",
            ["AcademicCatalog"] = "البيانات الجامعية",
            ["PhoneCountries"] = "مفاتيح الهواتف",
        };

    /// <summary>The Arabic labels of the sections a permission opens, in navigation order.</summary>
    public static IEnumerable<string> SectionLabelsFor(string permission) =>
        SectionsFor(permission).Select(section => SectionLabels.TryGetValue(section, out var label) ? label : section);

    /// <summary>
    /// Classifies a permission by what it lets the holder do, so the role editor can group and
    /// colour them consistently: <c>view</c> reads, <c>approve</c> decides, <c>manage</c> writes.
    /// </summary>
    public static string LevelOf(string permission)
    {
        if (permission.StartsWith("view-", StringComparison.Ordinal)) return "view";
        if (permission.StartsWith("approve-", StringComparison.Ordinal)) return "approve";
        return "manage";
    }

    /// <summary>
    /// Permissions that must never be handed to a custom role: they govern who may change
    /// roles, users and the security posture, so granting them would let a role escalate its
    /// own privileges or weaken the system. Only the built-in SuperAdmin holds them.
    /// </summary>
    public static readonly IReadOnlySet<string> SuperAdminOnly =
        new HashSet<string>(StringComparer.Ordinal) { ManageRoles, ManageFinanceSettings, ManageSecuritySettings, ManageCertificateExportSettings };

    /// <summary>
    /// One row of the permission matrix: a feature (resource) and the permission that governs each
    /// access level. A null cell means the feature has no permission at that level, and the editor
    /// renders an inert placeholder so the grid stays aligned.
    /// </summary>
    public sealed record PermissionRow(string Label, string? View, string? Manage, string? Approve);

    /// <summary>A titled band of matrix rows, rendered under one header that spans every column.</summary>
    public sealed record PermissionGroup(string Title, IReadOnlyList<PermissionRow> Rows);

    /// <summary>
    /// The permission matrix the role editor renders: rows are features, columns are the three access
    /// levels (view / manage / approve). It is the single source of truth for the editor layout — the
    /// completeness tests assert it exposes every grantable permission exactly once, and that each
    /// permission sits in the column matching <see cref="LevelOf"/>.
    /// </summary>
    public static readonly IReadOnlyList<PermissionGroup> Matrix = new List<PermissionGroup>
    {
        new("إدارة التدريب", new[]
        {
            new PermissionRow("التسجيلات", ViewRegistrations, ManageRegistrations, null),
            new PermissionRow("الدورات", ViewCourses, ManageCourses, null),
            new PermissionRow("المدربون", ViewInstructors, ManageInstructors, null),
            new PermissionRow("الفئات", ViewCategories, ManageCategories, null),
        }),
        new("الشهادات", new[]
        {
            new PermissionRow("الشهادات", ViewCertificates, IssueCertificates, null),
            new PermissionRow("تصاميم الشهادات", null, ManageCertificateDesigns, null),
        }),
        new("الإدارة المالية", new[]
        {
            new PermissionRow("الفواتير", ViewInvoices, ManageFinance, null),
            new PermissionRow("الدفعات", ViewPayments, ManagePayments, null),
            new PermissionRow("الأقساط والخطط", ViewInstallments, ManageInstallments, null),
            new PermissionRow("عهدة الموظف والتوريد", ViewCustody, null, ApproveCustody),
            new PermissionRow("الصندوق الرئيسي", ViewCashbox, ManageCashbox, null),
            new PermissionRow("استرداد الرسوم", null, ManageRefunds, ApproveRefunds),
            new PermissionRow("المصروفات", ViewExpenses, ManageExpenses, ApproveExpenses),
            new PermissionRow("المتأخرات وكشف الحساب", ViewReceivables, null, null),
            new PermissionRow("سجل التدقيق المالي", ViewFinancialAudit, null, null),
            new PermissionRow("لوحة المؤشرات المالية", ViewFinanceDashboard, null, null),
            new PermissionRow("تقارير الإيرادات", ViewReports, null, null),
        }),
        new("المحتوى", new[]
        {
            new PermissionRow("المنشورات", null, ManagePosts, null),
            new PermissionRow("الأخبار", null, ManageNews, null),
            new PermissionRow("الصفحة الرئيسية", null, ManageHomepage, null),
        }),
        new("النظام", new[]
        {
            new PermissionRow("المستخدمون", null, ManageUsers, null),
            new PermissionRow("سجل العمليات", ViewAuditLog, null, null),
            new PermissionRow("مراجعة سجلات التدقيق", ViewSecurityAudit, null, null),
            new PermissionRow("البيانات الجامعية", null, ManageAcademicCatalog, null),
            new PermissionRow("مفاتيح الهواتف", null, ManagePhoneCountries, null),
        }),
    };

    /// <summary>Every permission the matrix places in a cell, in row-major order.</summary>
    public static IEnumerable<string> MatrixPermissions =>
        Matrix.SelectMany(group => group.Rows)
              .SelectMany(row => new[] { row.View, row.Manage, row.Approve })
              .Where(name => name is not null)
              .Select(name => name!);
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
