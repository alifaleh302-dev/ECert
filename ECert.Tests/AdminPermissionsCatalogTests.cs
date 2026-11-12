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
    [InlineData("manage-cashbox", "Cashbox")]
    [InlineData("withdraw-cashbox", "Cashbox")]
    public void Permission_maps_to_its_section(string permission, string section)
    {
        Assert.Contains(section, AdminPermissions.SectionsFor(permission));
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

    private static ClaimsPrincipal Principal(string type, string value) =>
        new(new ClaimsIdentity(new[] { new Claim(type, value) }, "Test"));
}
