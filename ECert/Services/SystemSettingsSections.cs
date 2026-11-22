using System.Security.Claims;

namespace ECert.Services;

/// <summary>
/// One tab of the unified system-settings area. <see cref="Controller"/> and <see cref="Action"/>
/// decide which page lights the tab up; a null <see cref="Action"/> matches every action of the
/// controller, so list/detail pages of the same section keep the tab active.
/// </summary>
public sealed record SettingsSection(
    string Key,
    string Label,
    string Icon,
    string Controller,
    string? Action,
    string Url,
    string Description,
    string? Permission = null,
    bool SuperAdminOnly = false);

/// <summary>
/// The single source of truth for the settings tabs: the tab strip, the sidebar entry, the hub
/// redirect and the tests all read from here, so a tab can never be shown without its permission
/// or hidden while its page is still reachable.
/// </summary>
public static class SystemSettingsSections
{
    public static readonly IReadOnlyList<SettingsSection> All = new List<SettingsSection>
    {
        new("security", "الأمان", "bi-shield-lock", "SecuritySettings", "Index", "/SecuritySettings",
            "توكن تليجرام، سياسة الحظر، والمصادقة الثنائية.", SuperAdminOnly: true),
        new("backup", "النسخ الاحتياطي", "bi-cloud-arrow-up", "SecuritySettings", "Backup", "/SecuritySettings/Backup",
            "قناة الأرشيف، مفتاح التشفير، والجدولة اليومية.", SuperAdminOnly: true),
        new("export", "تصدير الشهادات", "bi-file-earmark-pdf", "CertificateExportSettings", null, "/CertificateExportSettings",
            "من يملك التحميل، الحد الأقصى لكل طلب، ومقاس الصفحة.",
            AdminPermissions.ManageCertificateExportSettings, SuperAdminOnly: true),
        new("academic", "البيانات الجامعية", "bi-mortarboard", "AcademicCatalog", null, "/AcademicCatalog",
            "الجامعات ثم الكليات ثم التخصصات والمستويات الدراسية.",
            AdminPermissions.ManageAcademicCatalog),
        new("phones", "مفاتيح الهواتف", "bi-phone", "PhoneCountries", null, "/PhoneCountries",
            "مفاتيح الدول وقواعد التحقق من أرقام الهواتف.",
            AdminPermissions.ManagePhoneCountries),
        new("finance", "الإعدادات المالية", "bi-sliders", "FinanceSettings", null, "/FinanceSettings",
            "العملة، اعتماد الاسترداد، العهدة، وإقفال الفترات.",
            AdminPermissions.ManageFinanceSettings, SuperAdminOnly: true),
    };

    public static bool CanAccess(ClaimsPrincipal user, SettingsSection section) =>
        user.IsSuperAdmin() || (section.Permission is not null && user.HasPermission(section.Permission));

    public static IReadOnlyList<SettingsSection> VisibleFor(ClaimsPrincipal user) =>
        All.Where(section => CanAccess(user, section)).ToList();

    /// <summary>The tab that owns the current request, or null for pages outside the settings area.</summary>
    public static SettingsSection? Find(string? controller, string? action) =>
        All.FirstOrDefault(section =>
            string.Equals(section.Controller, controller, StringComparison.OrdinalIgnoreCase)
            && (section.Action is null || string.Equals(section.Action, action, StringComparison.OrdinalIgnoreCase)));
}
