using System.Text.RegularExpressions;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// End-to-end tests for the role/permission flow, driven through the real HTTP surface: an admin
/// creates a role with a chosen set of permissions, creates a user bound to that role, and the user
/// then signs in. The tests assert that the sections the user can open match exactly the permissions
/// granted — this is the contract the "3 permissions but only 2 menus" bug broke.
/// </summary>
[Collection(AppCollection.Name)]
public class RolePermissionFlowTests
{
    private const string UserPassword = "User@12345";
    private readonly AppFixture _fixture;

    public RolePermissionFlowTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Admin_can_open_every_section()
    {
        var admin = await SignInAdminAsync();

        foreach (var section in Sections.All)
            Assert.True(await admin.CanOpenAsync(section.Path), $"admin should open {section.Path}");
    }

    [Fact]
    public async Task Role_editor_offers_every_grantable_permission()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Roles/Create");

        var offered = Regex.Matches(html, "name=\"PermissionIds\"").Count;
        var expected = AdminPermissions.Catalog.Count(d => !AdminPermissions.SuperAdminOnly.Contains(d.Name));

        Assert.Equal(expected, offered);
    }

    [Fact]
    public async Task A_role_granting_three_permissions_opens_exactly_three_sections()
    {
        // Regression: this used to show two menus because manage-finance mapped to no section.
        var granted = new[] { AdminPermissions.ManageRegistrations, AdminPermissions.IssueCertificates, AdminPermissions.ManageFinance };
        var expected = Sections.ExpectedFor(granted).Select(s => s.Name).ToHashSet();

        Assert.Equal(3, expected.Count);

        var user = await CreateUserWithRoleAsync(granted);
        await AssertSectionsAsync(user, expected);
    }

    [Theory]
    [MemberData(nameof(GrantCombinations))]
    public async Task Granted_permissions_match_visible_sections(string[] granted)
    {
        var user = await CreateUserWithRoleAsync(granted);
        var expected = Sections.ExpectedFor(granted).Select(s => s.Name).ToHashSet();
        await AssertSectionsAsync(user, expected);
    }

    public static IEnumerable<object[]> GrantCombinations()
    {
        yield return new object[] { new[] { AdminPermissions.ManageRegistrations } };
        yield return new object[] { new[] { AdminPermissions.ViewInvoices } };
        yield return new object[] { new[] { AdminPermissions.ManageFinance } };
        yield return new object[] { new[] { AdminPermissions.ManageRegistrations, AdminPermissions.ViewReports, AdminPermissions.ManagePosts } };
        yield return new object[] { new[] { AdminPermissions.ManageCashbox, AdminPermissions.ManagePhoneCountries } };
        yield return new object[] { new[] { AdminPermissions.ManageUsers, AdminPermissions.ViewAuditLog } };
    }

    [Fact]
    public async Task Manage_finance_alone_opens_invoices()
    {
        // A user granted only the finance umbrella must see the Invoices section.
        var user = await CreateUserWithRoleAsync(new[] { AdminPermissions.ManageFinance });
        var client = await SignInAsync(user.Username, user.Password);

        Assert.True(await client.CanOpenAsync("/Invoices"), "manage-finance should open /Invoices");
    }

    [Fact]
    public async Task View_invoices_alone_opens_invoices()
    {
        var user = await CreateUserWithRoleAsync(new[] { AdminPermissions.ViewInvoices });
        var client = await SignInAsync(user.Username, user.Password);

        Assert.True(await client.CanOpenAsync("/Invoices"), "view-invoices should open /Invoices");
    }

    [Fact]
    public async Task User_with_no_ungranted_section_access()
    {
        var user = await CreateUserWithRoleAsync(new[] { AdminPermissions.ManageRegistrations });
        var client = await SignInAsync(user.Username, user.Password);

        // The one granted section is reachable; every other admin section is denied.
        Assert.True(await client.CanOpenAsync("/Registrations"));
        Assert.False(await client.CanOpenAsync("/Users"));
        Assert.False(await client.CanOpenAsync("/Roles"));
        Assert.False(await client.CanOpenAsync("/Invoices"));
        Assert.False(await client.CanOpenAsync("/AuditLog"));
    }

    [Fact]
    public async Task Super_admin_only_permission_is_not_grantable()
    {
        // manage-roles must never appear in the role editor, so a custom role cannot escalate.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Roles/Create");
        Assert.DoesNotContain(AdminPermissions.ManageRoles, html);
    }

    private async Task AssertSectionsAsync(TestUser user, HashSet<string> expected)
    {
        var client = await SignInAsync(user.Username, user.Password);
        foreach (var section in Sections.All)
        {
            var canOpen = await client.CanOpenAsync(section.Path);
            if (expected.Contains(section.Name))
                Assert.True(canOpen, $"{user.Username} was granted {section.Name} but cannot open {section.Path}");
            else
                Assert.False(canOpen, $"{user.Username} was not granted {section.Name} but can open {section.Path}");
        }
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private async Task<AppClient> SignInAsync(string username, string password)
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync(username, password);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    /// <summary>Creates a role with the given permissions, then a user bound to it, over real HTTP.</summary>
    private async Task<TestUser> CreateUserWithRoleAsync(IEnumerable<string> permissionNames)
    {
        var admin = await SignInAdminAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roleName = $"دور {suffix}";
        var username = $"user{suffix}";

        var permissionIds = new List<int>();
        foreach (var name in permissionNames)
            permissionIds.Add(await _fixture.Factory.PermissionIdAsync(name));

        var roleFields = new List<KeyValuePair<string, string>>
        {
            new("RoleName", roleName),
            new("Description", "دور اختبار آلي")
        };
        roleFields.AddRange(permissionIds.Select(id => new KeyValuePair<string, string>("PermissionIds", id.ToString())));

        var roleResponse = await admin.PostFormAsync("/Roles/Create", "/Roles/Create", roleFields);
        roleResponse.EnsureSuccessStatusCode();
        Assert.True(await _fixture.Factory.RoleExistsAsync(roleName), $"role {roleName} was not created");

        var roleId = await _fixture.Factory.RoleIdAsync(roleName);

        var userFields = new List<KeyValuePair<string, string>>
        {
            new("Username", username),
            new("FullName", "مستخدم اختبار"),
            new("Email", $"{username}@example.com"),
            new("Phone", "0500000000"),
            new("Password", UserPassword),
            new("RoleId", roleId.ToString())
        };

        var userResponse = await admin.PostFormAsync("/Users/Create", "/Users/Create", userFields);
        userResponse.EnsureSuccessStatusCode();

        var userId = await _fixture.Factory.UserIdAsync(username);
        Assert.True(userId > 0, $"user {username} was not created");

        return new TestUser(username, UserPassword);
    }

    private sealed record TestUser(string Username, string Password);
}
