using System.Security.Claims;
using ECert.Services;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Guards the invariant behind the "granted a permission but the section never appears" bug:
/// every permission the role editor can grant must map to at least one section, and every
/// section must be reachable through a permission in the catalog.
/// </summary>
public class AdminPermissionsCatalogTests
{
    [Fact]
    public void Every_catalog_permission_opens_at_least_one_section()
    {
        var orphaned = AdminPermissions.Catalog
            .Where(definition => !AdminPermissions.SectionAccess.Values
                .Any(permissions => permissions.Contains(definition.Name, StringComparer.Ordinal)))
            .Select(definition => definition.Name)
            .ToList();

        Assert.True(orphaned.Count == 0,
            "These permissions can be granted in the role editor but open no section, so the user " +
            "would see fewer menus than permissions granted: " + string.Join(", ", orphaned));
    }

    [Fact]
    public void Every_section_permission_exists_in_the_catalog()
    {
        var catalogNames = AdminPermissions.Catalog.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = AdminPermissions.SectionAccess
            .SelectMany(entry => entry.Value)
            .Where(name => !catalogNames.Contains(name))
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0,
            "SectionAccess references permissions missing from the catalog: " + string.Join(", ", unknown));
    }

    [Fact]
    public void SuperAdmin_only_permissions_exist_in_the_catalog()
    {
        var catalogNames = AdminPermissions.Catalog.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(AdminPermissions.SuperAdminOnly, name => Assert.Contains(name, catalogNames));
    }

    [Theory]
    [InlineData("manage-finance", "Invoices")]
    [InlineData("view-invoices", "Invoices")]
    [InlineData("view-custody", "Custody")]
    [InlineData("manage-refunds", "Refunds")]
    [InlineData("view-receivables", "Receivables")]
    [InlineData("view-financial-audit", "FinancialAudit")]
    public void Permission_maps_to_its_section(string permission, string section)
    {
        Assert.Contains(section, AdminPermissions.SectionsFor(permission));
    }

    [Fact]
    public void Financial_permissions_are_all_defined_in_the_catalog()
    {
        string[] financial =
        {
            AdminPermissions.ViewCustody, AdminPermissions.ManageRefunds,
            AdminPermissions.ViewReceivables, AdminPermissions.ViewFinancialAudit
        };
        var catalogNames = AdminPermissions.Catalog.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        Assert.All(financial, name => Assert.Contains(name, catalogNames));
    }

    [Theory]
    [InlineData(AdminPermissions.ViewCashbox, "Cashbox")]
    [InlineData(AdminPermissions.ManageCashbox, "Cashbox")]
    [InlineData(AdminPermissions.ApproveCustody, "Custody")]
    [InlineData(AdminPermissions.ApproveRefunds, "Refunds")]
    [InlineData(AdminPermissions.ApproveExpenses, "Expenses")]
    [InlineData(AdminPermissions.ViewExpenses, "Expenses")]
    [InlineData(AdminPermissions.ViewRegistrations, "Registrations")]
    [InlineData(AdminPermissions.ViewPayments, "Payments")]
    [InlineData(AdminPermissions.ViewInstallments, "Installments")]
    public void Granular_permission_maps_to_its_section(string permission, string section)
    {
        Assert.Contains(section, AdminPermissions.SectionsFor(permission));
    }

    [Fact]
    public void Read_only_permissions_are_not_super_admin_only()
    {
        // A read-only grant must be delegable: it must never sit in the SuperAdmin-only set.
        string[] readOnly =
        {
            AdminPermissions.ViewRegistrations, AdminPermissions.ViewCourses,
            AdminPermissions.ViewInstructors, AdminPermissions.ViewCategories,
            AdminPermissions.ViewCertificates, AdminPermissions.ViewPayments,
            AdminPermissions.ViewExpenses, AdminPermissions.ViewInstallments,
            AdminPermissions.ViewCashbox
        };
        Assert.All(readOnly, name => Assert.DoesNotContain(name, AdminPermissions.SuperAdminOnly));
    }

    [Fact]
    public void Cashbox_is_reachable_through_grantable_permissions()
    {
        // The main fund is no longer SuperAdmin-only: it is unlocked by view/manage-cashbox.
        var cashboxViewer = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(AdminPrincipalExtensions.PermissionClaimType, AdminPermissions.ViewCashbox)
        }, "Test"));
        Assert.True(cashboxViewer.CanAccessSection("Cashbox"));
    }

    [Fact]
    public void The_finance_preset_grants_the_new_financial_permissions()
    {
        var finance = AdminPermissions.Presets.Single(p => p.Key == "finance");
        Assert.Contains(AdminPermissions.ViewCustody, finance.Permissions);
        Assert.Contains(AdminPermissions.ManageRefunds, finance.Permissions);
        Assert.Contains(AdminPermissions.ViewReceivables, finance.Permissions);
        Assert.Contains(AdminPermissions.ViewFinancialAudit, finance.Permissions);
    }

    [Fact]
    public void The_cashier_preset_manages_and_approves_but_stays_non_system()
    {
        var cashier = AdminPermissions.Presets.Single(p => p.Key == "cashier");
        Assert.Contains(AdminPermissions.ManageCashbox, cashier.Permissions);
        Assert.Contains(AdminPermissions.ApproveExpenses, cashier.Permissions);
        Assert.Contains(AdminPermissions.ApproveRefunds, cashier.Permissions);
        Assert.Contains(AdminPermissions.ApproveCustody, cashier.Permissions);
        Assert.DoesNotContain(AdminPermissions.ManageUsers, cashier.Permissions);
        Assert.DoesNotContain(AdminPermissions.ManageRoles, cashier.Permissions);
    }

    [Fact]
    public void The_read_only_presets_grant_no_write_permission()
    {
        foreach (var key in new[] { "finance_readonly", "hr_readonly" })
        {
            var preset = AdminPermissions.Presets.Single(p => p.Key == key);
            Assert.All(preset.Permissions, name => Assert.StartsWith("view-", name));
        }
    }

    [Fact]
    public void Super_admin_can_access_every_known_section()
    {
        var superAdmin = Principal(ClaimTypes.Role, AdminPrincipalExtensions.SuperAdminRole);
        Assert.All(AdminPermissions.SectionAccess.Keys, section => Assert.True(superAdmin.CanAccessSection(section)));
    }

    [Fact]
    public void Section_without_its_permission_is_denied()
    {
        var user = Principal(AdminPrincipalExtensions.PermissionClaimType, AdminPermissions.ManageRegistrations);
        Assert.True(user.CanAccessSection("Registrations"));
        Assert.False(user.CanAccessSection("Invoices"));
    }

    [Fact]
    public void Unknown_section_is_super_admin_only()
    {
        var user = Principal(AdminPrincipalExtensions.PermissionClaimType, AdminPermissions.ManageRegistrations);
        Assert.False(user.CanAccessSection("SomeFutureController"));
    }

    [Fact]
    public void Every_preset_only_grants_known_permissions()
    {
        var catalogNames = AdminPermissions.Catalog.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = AdminPermissions.Presets
            .SelectMany(preset => preset.Permissions)
            .Where(name => !catalogNames.Contains(name))
            .Distinct()
            .ToList();

        Assert.True(unknown.Count == 0,
            "Role presets reference permissions missing from the catalog: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Presets_never_hand_out_super_admin_only_permissions()
    {
        var leaked = AdminPermissions.Presets
            .Where(preset => preset.Permissions.Any(AdminPermissions.SuperAdminOnly.Contains))
            .Select(preset => preset.Key)
            .ToList();

        Assert.True(leaked.Count == 0,
            "These presets would let a custom role escalate its own privileges: " + string.Join(", ", leaked));
    }

    [Fact]
    public void Preset_keys_are_unique()
    {
        var keys = AdminPermissions.Presets.Select(preset => preset.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_section_has_a_human_readable_label()
    {
        // The role editor explains which pages a permission opens, so a missing label would leak a
        // raw controller name into the UI.
        var unlabelled = AdminPermissions.SectionAccess.Keys
            .Where(section => !AdminPermissions.SectionLabels.ContainsKey(section))
            .ToList();

        Assert.True(unlabelled.Count == 0,
            "These sections have no Arabic label and would show as a controller name: " + string.Join(", ", unlabelled));
    }

    [Fact]
    public void Permission_level_follows_the_name_prefix()
    {
        Assert.Equal("view", AdminPermissions.LevelOf(AdminPermissions.ViewCashbox));
        Assert.Equal("approve", AdminPermissions.LevelOf(AdminPermissions.ApproveRefunds));
        Assert.Equal("manage", AdminPermissions.LevelOf(AdminPermissions.ManagePayments));
        Assert.Equal("manage", AdminPermissions.LevelOf(AdminPermissions.IssueCertificates));
    }

    [Fact]
    public void Every_permission_level_is_one_of_the_three_known_values()
    {
        var unexpected = AdminPermissions.Catalog
            .Select(definition => AdminPermissions.LevelOf(definition.Name))
            .Distinct()
            .Where(level => level is not ("view" or "manage" or "approve"))
            .ToList();

        Assert.True(unexpected.Count == 0,
            "The role editor colours permissions by level; unexpected levels: " + string.Join(", ", unexpected));
    }

    [Fact]
    public void Section_labels_for_a_permission_match_its_sections()
    {
        var labels = AdminPermissions.SectionLabelsFor(AdminPermissions.ViewCashbox).ToList();
        Assert.Contains("الصندوق", labels);

        var approveLabels = AdminPermissions.SectionLabelsFor(AdminPermissions.ApproveRefunds).ToList();
        Assert.Contains("استرداد الرسوم", approveLabels);
    }

    private static ClaimsPrincipal Principal(string type, string value) =>
        new(new ClaimsIdentity(new[] { new Claim(type, value) }, "Test"));
}
