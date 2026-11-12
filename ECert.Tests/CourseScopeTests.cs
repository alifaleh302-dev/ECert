using ECert.Services;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Course-level data isolation for administrative users. A user with at least one course assignment
/// must see and act on only those courses; a user with none stays unrestricted. These tests drive
/// the real HTTP surface so they cover the controller guards, the query filters and the sidebar,
/// not just the helper that computes the scope.
/// </summary>
[Collection(AppCollection.Name)]
public class CourseScopeTests
{
    private const string UserPassword = "User@12345";
    private readonly AppFixture _fixture;

    public CourseScopeTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Restricted_user_sees_only_assigned_course_invoices()
    {
        var allowedCourse = await _fixture.Factory.CreateCourseAsync($"دورة مسموحة {Guid.NewGuid():N}"[..30]);
        var otherCourse = await _fixture.Factory.CreateCourseAsync($"دورة أخرى {Guid.NewGuid():N}"[..30]);
        var allowedInvoice = await _fixture.Factory.CreateInvoiceForCourseAsync(allowedCourse);
        await _fixture.Factory.CreateInvoiceForCourseAsync(otherCourse);

        var user = await CreateFinanceUserAsync();
        await _fixture.Factory.AssignCoursesAsync(user.Username, allowedCourse);

        var client = await SignInAsync(user.Username, user.Password);
        var html = await client.GetStringAsync("/Invoices");

        Assert.Contains(await _fixture.Factory.InvoiceNumberOfAsync(allowedInvoice), html);
        Assert.DoesNotContain(await _fixture.Factory.InvoiceNumberForCourseAsync(otherCourse), html);
    }

    [Fact]
    public async Task Restricted_user_cannot_open_an_invoice_outside_their_scope()
    {
        var allowedCourse = await _fixture.Factory.CreateCourseAsync($"دورة مسموحة {Guid.NewGuid():N}"[..30]);
        var otherCourse = await _fixture.Factory.CreateCourseAsync($"دورة أخرى {Guid.NewGuid():N}"[..30]);
        await _fixture.Factory.CreateInvoiceForCourseAsync(allowedCourse);
        var otherInvoice = await _fixture.Factory.CreateInvoiceForCourseAsync(otherCourse);

        var user = await CreateFinanceUserAsync();
        await _fixture.Factory.AssignCoursesAsync(user.Username, allowedCourse);
        var client = await SignInAsync(user.Username, user.Password);

        Assert.True(await client.IsDeniedAsync($"/Invoices/Details/{otherInvoice}"));
    }

    [Fact]
    public async Task Restricted_user_cannot_record_a_payment_on_an_out_of_scope_invoice()
    {
        var allowedCourse = await _fixture.Factory.CreateCourseAsync($"دورة مسموحة {Guid.NewGuid():N}"[..30]);
        var otherCourse = await _fixture.Factory.CreateCourseAsync($"دورة أخرى {Guid.NewGuid():N}"[..30]);
        var allowedInvoice = await _fixture.Factory.CreateInvoiceForCourseAsync(allowedCourse);
        var otherInvoice = await _fixture.Factory.CreateInvoiceForCourseAsync(otherCourse);

        var user = await CreateFinanceUserAsync();
        await _fixture.Factory.AssignCoursesAsync(user.Username, allowedCourse);
        var client = await SignInAsync(user.Username, user.Password);

        // The token is taken from an in-scope page: the out-of-scope Add page is itself forbidden.
        var response = await client.PostFormAsync($"/Invoices/Details/{allowedInvoice}", "/Payments/Add", new[]
        {
            new KeyValuePair<string, string>("InvoiceId", otherInvoice.ToString()),
            new KeyValuePair<string, string>("Amount", "100"),
            new KeyValuePair<string, string>("PaymentMethod", "Cash")
        });

        Assert.True(AppClient.IsDenied(response));
        var (paid, _) = await _fixture.Factory.GetInvoiceAsync(otherInvoice);
        Assert.Equal(0m, paid);
    }

    [Fact]
    public async Task Restricted_user_sees_only_assigned_course_registrations()
    {
        var allowedCourse = await _fixture.Factory.CreateCourseAsync($"دورة مسموحة {Guid.NewGuid():N}"[..30]);
        var otherCourse = await _fixture.Factory.CreateCourseAsync($"دورة أخرى {Guid.NewGuid():N}"[..30]);
        await _fixture.Factory.CreateInvoiceForCourseAsync(allowedCourse);
        await _fixture.Factory.CreateInvoiceForCourseAsync(otherCourse);

        var user = await CreateRegistrationsUserAsync();
        await _fixture.Factory.AssignCoursesAsync(user.Username, allowedCourse);
        var client = await SignInAsync(user.Username, user.Password);

        var html = await client.GetStringAsync("/Registrations");
        Assert.Contains($"value=\"{allowedCourse}\"", html);
        Assert.DoesNotContain($"value=\"{otherCourse}\"", html);
    }

    [Fact]
    public async Task Unrestricted_user_sees_every_course()
    {
        var courseA = await _fixture.Factory.CreateCourseAsync($"دورة أ {Guid.NewGuid():N}"[..30]);
        var courseB = await _fixture.Factory.CreateCourseAsync($"دورة ب {Guid.NewGuid():N}"[..30]);
        await _fixture.Factory.CreateInvoiceForCourseAsync(courseA);
        await _fixture.Factory.CreateInvoiceForCourseAsync(courseB);

        var user = await CreateFinanceUserAsync();
        var client = await SignInAsync(user.Username, user.Password);

        var html = await client.GetStringAsync("/Invoices");
        Assert.Contains(await _fixture.Factory.InvoiceNumberForCourseAsync(courseA), html);
        Assert.Contains(await _fixture.Factory.InvoiceNumberForCourseAsync(courseB), html);
    }

    [Fact]
    public async Task Assigning_courses_takes_effect_on_the_next_login()
    {
        var course = await _fixture.Factory.CreateCourseAsync($"دورة {Guid.NewGuid():N}"[..30]);
        await _fixture.Factory.CreateInvoiceForCourseAsync(course);

        var user = await CreateFinanceUserAsync();
        // The account is created unrestricted (the creator is a SuperAdmin who chose no courses),
        // then the SuperAdmin narrows it — the scope must be read fresh at each sign-in.
        await _fixture.Factory.AssignCoursesAsync(user.Username, course);

        var client = await SignInAsync(user.Username, user.Password);
        var html = await client.GetStringAsync("/Invoices");
        Assert.Contains(await _fixture.Factory.InvoiceNumberForCourseAsync(course), html);
    }

    [Fact]
    public async Task Category_grant_confines_the_user_to_that_categorys_courses()
    {
        var categoryId = await _fixture.Factory.CreateCategoryAsync($"فئة {Guid.NewGuid():N}"[..30]);
        var inCategory = await _fixture.Factory.CreateCourseInCategoryAsync($"دورة داخل الفئة {Guid.NewGuid():N}"[..30], categoryId);
        var otherCourse = await _fixture.Factory.CreateCourseAsync($"دورة خارج الفئة {Guid.NewGuid():N}"[..30]);
        var inCategoryInvoice = await _fixture.Factory.CreateInvoiceForCourseAsync(inCategory);
        await _fixture.Factory.CreateInvoiceForCourseAsync(otherCourse);

        var user = await CreateFinanceUserAsync();
        await _fixture.Factory.AssignCategoriesAsync(user.Username, categoryId);
        var client = await SignInAsync(user.Username, user.Password);

        var html = await client.GetStringAsync("/Invoices");
        Assert.Contains(await _fixture.Factory.InvoiceNumberOfAsync(inCategoryInvoice), html);
        Assert.DoesNotContain(await _fixture.Factory.InvoiceNumberForCourseAsync(otherCourse), html);
    }

    [Fact]
    public async Task Category_grant_covers_courses_added_to_the_category_later()
    {
        var categoryId = await _fixture.Factory.CreateCategoryAsync($"فئة {Guid.NewGuid():N}"[..30]);
        var user = await CreateFinanceUserAsync();
        await _fixture.Factory.AssignCategoriesAsync(user.Username, categoryId);

        // The course is added after the grant was made: signing in afterwards must still see it.
        var laterCourse = await _fixture.Factory.CreateCourseInCategoryAsync($"دورة لاحقة {Guid.NewGuid():N}"[..30], categoryId);
        var laterInvoice = await _fixture.Factory.CreateInvoiceForCourseAsync(laterCourse);

        var client = await SignInAsync(user.Username, user.Password);
        var html = await client.GetStringAsync("/Invoices");
        Assert.Contains(await _fixture.Factory.InvoiceNumberOfAsync(laterInvoice), html);
    }

    [Fact]
    public async Task Category_grant_blocks_a_course_moved_out_of_the_category()
    {
        var grantedCategory = await _fixture.Factory.CreateCategoryAsync($"فئة مسموحة {Guid.NewGuid():N}"[..30]);
        var otherCategory = await _fixture.Factory.CreateCategoryAsync($"فئة أخرى {Guid.NewGuid():N}"[..30]);
        var course = await _fixture.Factory.CreateCourseInCategoryAsync($"دورة متنقلة {Guid.NewGuid():N}"[..30], grantedCategory);
        var invoice = await _fixture.Factory.CreateInvoiceForCourseAsync(course);

        var user = await CreateFinanceUserAsync();
        await _fixture.Factory.AssignCategoriesAsync(user.Username, grantedCategory);

        var before = await SignInAsync(user.Username, user.Password);
        Assert.Contains(await _fixture.Factory.InvoiceNumberOfAsync(invoice), await before.GetStringAsync("/Invoices"));

        await _fixture.Factory.MoveCourseToCategoryAsync(course, otherCategory);

        var after = await SignInAsync(user.Username, user.Password);
        Assert.DoesNotContain(await _fixture.Factory.InvoiceNumberOfAsync(invoice), await after.GetStringAsync("/Invoices"));
    }

    [Fact]
    public async Task Course_and_category_grants_combine_into_one_scope()
    {
        var categoryId = await _fixture.Factory.CreateCategoryAsync($"فئة {Guid.NewGuid():N}"[..30]);
        var inCategory = await _fixture.Factory.CreateCourseInCategoryAsync($"دورة الفئة {Guid.NewGuid():N}"[..30], categoryId);
        var directCourse = await _fixture.Factory.CreateCourseAsync($"دورة مباشرة {Guid.NewGuid():N}"[..30]);
        var otherCourse = await _fixture.Factory.CreateCourseAsync($"دورة مستبعدة {Guid.NewGuid():N}"[..30]);
        var inCategoryInvoice = await _fixture.Factory.CreateInvoiceForCourseAsync(inCategory);
        var directInvoice = await _fixture.Factory.CreateInvoiceForCourseAsync(directCourse);
        await _fixture.Factory.CreateInvoiceForCourseAsync(otherCourse);

        var user = await CreateFinanceUserAsync();
        await _fixture.Factory.AssignCategoriesAsync(user.Username, categoryId);
        await _fixture.Factory.AssignCoursesAsync(user.Username, directCourse);
        var client = await SignInAsync(user.Username, user.Password);

        var html = await client.GetStringAsync("/Invoices");
        Assert.Contains(await _fixture.Factory.InvoiceNumberOfAsync(inCategoryInvoice), html);
        Assert.Contains(await _fixture.Factory.InvoiceNumberOfAsync(directInvoice), html);
        Assert.DoesNotContain(await _fixture.Factory.InvoiceNumberForCourseAsync(otherCourse), html);
    }

    [Fact]
    public async Task A_category_with_no_courses_yet_confines_the_user_to_nothing()
    {
        var emptyCategory = await _fixture.Factory.CreateCategoryAsync($"فئة فارغة {Guid.NewGuid():N}"[..30]);
        var course = await _fixture.Factory.CreateCourseAsync($"دورة {Guid.NewGuid():N}"[..30]);
        var invoice = await _fixture.Factory.CreateInvoiceForCourseAsync(course);

        var user = await CreateFinanceUserAsync();
        await _fixture.Factory.AssignCategoriesAsync(user.Username, emptyCategory);
        var client = await SignInAsync(user.Username, user.Password);

        // Granted a category that holds nothing means "confined to nothing", never "everything".
        var html = await client.GetStringAsync("/Invoices");
        Assert.DoesNotContain(await _fixture.Factory.InvoiceNumberOfAsync(invoice), html);
    }

    [Fact]
    public async Task The_user_picker_lists_categories_and_courses_with_search_and_select_all()
    {
        var categoryId = await _fixture.Factory.CreateCategoryAsync($"فئة الواجهة {Guid.NewGuid():N}"[..30]);
        var course = await _fixture.Factory.CreateCourseInCategoryAsync($"دورة الواجهة {Guid.NewGuid():N}"[..30], categoryId);

        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Users/Create");

        Assert.Contains("data-scope-picker", html);
        Assert.Contains("data-scope-search", html);
        Assert.Contains("data-scope-filter", html);
        Assert.Contains("data-scope-cat-toggle", html);
        Assert.Contains("data-scope-course-toggle", html);
        Assert.Contains($"name=\"CategoryIds\" value=\"{categoryId}\"", html);
        Assert.Contains($"name=\"CourseIds\" value=\"{course}\"", html);
    }

    [Fact]
    public async Task The_edit_picker_reflects_the_existing_category_grant()
    {
        var categoryId = await _fixture.Factory.CreateCategoryAsync($"فئة التعديل {Guid.NewGuid():N}"[..30]);
        var user = await CreateFinanceUserAsync();
        await _fixture.Factory.AssignCategoriesAsync(user.Username, categoryId);
        var userId = await _fixture.Factory.UserIdAsync(user.Username);

        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync($"/Users/Edit/{userId}");

        Assert.Contains($"name=\"CategoryIds\" value=\"{categoryId}\" checked=\"checked\"", html);
    }

    [Fact]
    public async Task Saving_category_grants_from_the_edit_form_persists_and_applies()
    {
        var categoryId = await _fixture.Factory.CreateCategoryAsync($"فئة محفوظة {Guid.NewGuid():N}"[..30]);
        var course = await _fixture.Factory.CreateCourseInCategoryAsync($"دورة محفوظة {Guid.NewGuid():N}"[..30], categoryId);
        var invoice = await _fixture.Factory.CreateInvoiceForCourseAsync(course);
        var otherCourse = await _fixture.Factory.CreateCourseAsync($"دورة غير مسموحة {Guid.NewGuid():N}"[..30]);
        var otherInvoice = await _fixture.Factory.CreateInvoiceForCourseAsync(otherCourse);

        var user = await CreateFinanceUserAsync();
        var userId = await _fixture.Factory.UserIdAsync(user.Username);
        var roleId = await _fixture.Factory.RoleIdAsync(
            await _fixture.Factory.RoleNameOfUserAsync(user.Username));

        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync($"/Users/Edit/{userId}", $"/Users/Edit/{userId}", new[]
        {
            new KeyValuePair<string, string>("UserId", userId.ToString()),
            new KeyValuePair<string, string>("FullName", "مستخدم نطاق"),
            new KeyValuePair<string, string>("Email", $"{user.Username}@example.com"),
            new KeyValuePair<string, string>("Phone", "0500000000"),
            new KeyValuePair<string, string>("RoleId", roleId.ToString()),
            new KeyValuePair<string, string>("IsActive", "true"),
            new KeyValuePair<string, string>("CategoryIds", categoryId.ToString())
        });
        response.EnsureSuccessStatusCode();

        var client = await SignInAsync(user.Username, user.Password);
        var html = await client.GetStringAsync("/Invoices");
        Assert.Contains(await _fixture.Factory.InvoiceNumberOfAsync(invoice), html);
        Assert.DoesNotContain(await _fixture.Factory.InvoiceNumberOfAsync(otherInvoice), html);
    }

    private Task<TestUser> CreateFinanceUserAsync() =>
        CreateUserWithRoleAsync(new[]
        {
            AdminPermissions.ManageFinance,
            AdminPermissions.ManagePayments,
            AdminPermissions.ViewReports
        });

    private Task<TestUser> CreateRegistrationsUserAsync() =>
        CreateUserWithRoleAsync(new[] { AdminPermissions.ManageRegistrations });

    private async Task<AppClient> SignInAsync(string username, string password)
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync(username, password);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

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
            new("Description", "دور اختبار نطاق الدورات")
        };
        roleFields.AddRange(permissionIds.Select(id => new KeyValuePair<string, string>("PermissionIds", id.ToString())));

        var roleResponse = await admin.PostFormAsync("/Roles/Create", "/Roles/Create", roleFields);
        roleResponse.EnsureSuccessStatusCode();
        var roleId = await _fixture.Factory.RoleIdAsync(roleName);

        var userFields = new List<KeyValuePair<string, string>>
        {
            new("Username", username),
            new("FullName", "مستخدم نطاق"),
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
