using System.Text.RegularExpressions;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// The unified settings area: one sidebar entry, a permission-aware tab strip, and a hub that sends
/// each role to the first tab it may open. These tests drive the real HTTP surface so a tab can
/// never appear without its permission or disappear while its page is still reachable.
/// </summary>
[Collection(AppCollection.Name)]
public class SystemSettingsTests
{
    private const string UserPassword = "User@12345";
    private readonly AppFixture _fixture;

    public SystemSettingsTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Hub_redirects_the_super_admin_to_the_first_tab()
    {
        var admin = await SignInAdminAsync();
        var response = await admin.GetAsync("/SystemSettings");

        Assert.Equal("/SecuritySettings", response.RequestMessage?.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Super_admin_sees_every_settings_tab()
    {
        var admin = await SignInAdminAsync();
        var html = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync("/SecuritySettings"));

        Assert.Equal(SystemSettingsSections.All.Count, Regex.Matches(html, "class=\"settings-tab(?: is-active)?\"").Count);
        foreach (var section in SystemSettingsSections.All)
            Assert.Contains(section.Label, html);
    }

    [Fact]
    public async Task Sidebar_shows_one_settings_entry_instead_of_four()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Dashboard");

        Assert.Contains("href=\"/SystemSettings\"", html);
        Assert.DoesNotContain("href=\"/SecuritySettings\"", html);
        Assert.DoesNotContain("href=\"/CertificateExportSettings\"", html);
        Assert.DoesNotContain("href=\"/AcademicCatalog\"", html);
        Assert.DoesNotContain("href=\"/PhoneCountries\"", html);
    }

    [Fact]
    public async Task The_active_tab_is_marked_in_the_strip()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/CertificateExportSettings");

        Assert.Contains("class=\"settings-tab is-active\" href=\"/CertificateExportSettings\"", html);
        Assert.Single(Regex.Matches(html, "settings-tab is-active").Cast<System.Text.RegularExpressions.Match>());
    }

    [Fact]
    public async Task A_role_with_only_phone_access_lands_on_the_phone_tab()
    {
        var user = await CreateUserWithRoleAsync(new[] { AdminPermissions.ManagePhoneCountries });
        var client = await SignInAsync(user.Username, user.Password);

        var response = await client.GetAsync("/SystemSettings");
        Assert.Equal("/PhoneCountries", response.RequestMessage?.RequestUri?.AbsolutePath);

        var html = await client.GetStringAsync("/PhoneCountries");
        Assert.Contains("class=\"settings-tab is-active\" href=\"/PhoneCountries\"", html);
        Assert.DoesNotContain("href=\"/SecuritySettings\"", html);
        Assert.DoesNotContain("href=\"/AcademicCatalog\"", html);
    }

    [Fact]
    public async Task Settings_tabs_are_hidden_from_a_role_that_cannot_open_them()
    {
        var user = await CreateUserWithRoleAsync(new[] { AdminPermissions.ManageAcademicCatalog });
        var client = await SignInAsync(user.Username, user.Password);

        Assert.True(await client.CanOpenAsync("/AcademicCatalog"));
        Assert.False(await client.CanOpenAsync("/SecuritySettings"));
        Assert.False(await client.CanOpenAsync("/SecuritySettings/Backup"));
        Assert.False(await client.CanOpenAsync("/CertificateExportSettings"));
        Assert.False(await client.CanOpenAsync("/FinanceSettings"));

        var html = await client.GetStringAsync("/AcademicCatalog");
        Assert.Contains("href=\"/AcademicCatalog\"", html);
        Assert.DoesNotContain("href=\"/PhoneCountries\"", html);
    }

    [Fact]
    public async Task Every_settings_tab_is_reachable_by_the_super_admin()
    {
        var admin = await SignInAdminAsync();
        foreach (var section in SystemSettingsSections.All)
            Assert.True(await admin.CanOpenAsync(section.Url), $"admin should open {section.Url}");
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

    private async Task<TestUser> CreateUserWithRoleAsync(IEnumerable<string> permissionNames)
    {
        var admin = await SignInAdminAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roleName = $"دور إعدادات {suffix}";
        var username = $"settings{suffix}";

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
