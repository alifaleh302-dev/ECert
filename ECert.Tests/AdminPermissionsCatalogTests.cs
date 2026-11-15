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
    public void Matrix_covers_every_grantable_permission_exactly_once()
    {
        var grantable = AdminPermissions.Catalog
            .Where(d => !AdminPermissions.SuperAdminOnly.Contains(d.Name))
            .Select(d => d.Name)
            .ToList();
        var inMatrix = AdminPermissions.MatrixPermissions.ToList();

        Assert.Equal(grantable.Count, inMatrix.Count);
        Assert.Empty(grantable.Except(inMatrix, StringComparer.Ordinal));
        Assert.Empty(inMatrix.Except(grantable, StringComparer.Ordinal));
        Assert.Equal(inMatrix.Count, inMatrix.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_matrix_cell_matches_its_column_level()
    {
        // A permission placed in the wrong column would offer the wrong access level in the editor.
        foreach (var group in AdminPermissions.Matrix)
        {
            foreach (var row in group.Rows)
            {
                if (row.View is not null) Assert.Equal("view", AdminPermissions.LevelOf(row.View));
                if (row.Manage is not null) Assert.Equal("manage", AdminPermissions.LevelOf(row.Manage));
                if (row.Approve is not null) Assert.Equal("approve", AdminPermissions.LevelOf(row.Approve));
            }
        }
    }

    [Fact]
    public void Matrix_never_exposes_a_super_admin_only_permission()
    {
        var leaked = AdminPermissions.MatrixPermissions
            .Where(AdminPermissions.SuperAdminOnly.Contains)
            .ToList();

        Assert.True(leaked.Count == 0,
            "The matrix would let a custom role escalate its own privileges: " + string.Join(", ", leaked));
    }

    [Fact]
    public void Matrix_group_titles_are_unique_and_non_empty()
    {
        var titles = AdminPermissions.Matrix.Select(group => group.Title).ToList();
        Assert.All(titles, title => Assert.False(string.IsNullOrWhiteSpace(title)));
        Assert.Equal(titles.Count, titles.Distinct(StringComparer.Ordinal).Count());
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
