using System.Net;
using ECert.Data;
using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Trainees are <see cref="Registration"/> rows shown under "التسجيلات". A user holding
/// <c>manage-registrations</c> must be able to add one and to correct an existing one without
/// touching the invoice, the fee snapshot or the request status; a read-only user must not reach
/// either form.
/// </summary>
[Collection(AppCollection.Name)]
public class TraineeManagementTests
{
    private const string UserPassword = "User@12345";
    private readonly AppFixture _fixture;

    public TraineeManagementTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Manager_adds_a_trainee_as_a_pending_request()
    {
        var courseId = await _fixture.Factory.CreateCourseAsync($"دورة إضافة {Guid.NewGuid():N}"[..24]);
        var chain = await CreateAcademicChainAsync();
        var manager = await SignInWithPermissionsAsync(AdminPermissions.ManageRegistrations);
        var name = $"متدرب جديد {Guid.NewGuid():N}"[..24];

        var response = await manager.PostFormAsync("/Registrations/Create", "/Registrations/Create", new Dictionary<string, string>
        {
            ["FullNameArabic"] = name,
            ["FullNameEnglish"] = "New Trainee",
            ["Phone"] = "0771234567",
            ["Gender"] = "Male",
            ["Email"] = "trainee@example.com",
            ["HeardFrom"] = "اختبار",
            ["CourseId"] = courseId.ToString(),
            ["UniversityId"] = chain.UniversityId.ToString(),
            ["CollegeId"] = chain.CollegeId.ToString(),
            ["AcademicSpecializationId"] = chain.SpecializationId.ToString(),
            ["AcademicLevel"] = chain.Level
        });

        response.EnsureSuccessStatusCode();
        Assert.StartsWith("/Registrations/Details/", response.RequestMessage!.RequestUri!.AbsolutePath);

        var registration = await LoadRegistrationAsync(name);
        Assert.Equal("Pending", registration.Status);
        Assert.Equal(courseId, registration.CourseId);
        Assert.Equal("+967771234567", registration.Phone);
        Assert.Equal(name, registration.FullName);
        Assert.StartsWith("REG-", registration.RequestNumber);
        Assert.Equal(chain.UniversityName, registration.UniversityNameSnapshot);
        Assert.Equal(chain.CollegeName, registration.CollegeNameSnapshot);
        Assert.Equal(chain.SpecializationName, registration.SpecializationNameSnapshot);
        Assert.Equal(chain.Level, registration.AcademicLevel);
    }

    [Fact]
    public async Task Manager_adds_a_trainee_and_accepts_immediately_with_an_invoice()
    {
        var courseId = await _fixture.Factory.CreateCourseAsync($"دورة اعتماد {Guid.NewGuid():N}"[..24]);
        var manager = await SignInWithPermissionsAsync(AdminPermissions.ManageRegistrations);
        var name = $"متدرب معتمد {Guid.NewGuid():N}"[..24];

        var response = await manager.PostFormAsync("/Registrations/Create", "/Registrations/Create", new Dictionary<string, string>
        {
            ["FullNameArabic"] = name,
            ["FullNameEnglish"] = "Accepted Trainee",
            ["Phone"] = "0779998877",
            ["Gender"] = "Female",
            ["CourseId"] = courseId.ToString(),
            ["AcceptImmediately"] = "true"
        });

        response.EnsureSuccessStatusCode();

        var registration = await LoadRegistrationAsync(name);
        Assert.Equal("Accepted", registration.Status);
        Assert.NotNull(registration.AcceptedDate);
        Assert.NotNull(registration.ProcessedBy);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        Assert.True(await db.Invoices.AnyAsync(i => i.RegistrationId == registration.RegistrationId),
            "an accepted trainee must get an invoice");
    }

    [Fact]
    public async Task Editing_a_trainee_updates_personal_data_and_returns_to_the_list()
    {
        var courseId = await _fixture.Factory.CreateCourseAsync($"دورة تعديل {Guid.NewGuid():N}"[..24]);
        var registrationId = await CreateRegistrationAsync(courseId, "متدرب قديم");
        var manager = await SignInWithPermissionsAsync(AdminPermissions.ManageRegistrations);
        var correctedName = UniqueName("اسم مصحح");

        var response = await manager.PostFormAsync($"/Registrations/Edit/{registrationId}", $"/Registrations/Edit/{registrationId}",
            new Dictionary<string, string>
            {
                ["FullNameArabic"] = correctedName,
                ["FullNameEnglish"] = "Corrected Name",
                ["Phone"] = "0733445566",
                ["Gender"] = "Male",
                ["Email"] = "fixed@example.com",
                ["CourseId"] = courseId.ToString(),
                ["ReturnUrl"] = "/Registrations?status=Pending"
            });

        response.EnsureSuccessStatusCode();
        Assert.Equal("/Registrations", response.RequestMessage!.RequestUri!.AbsolutePath);

        var registration = await LoadRegistrationAsync(correctedName);
        Assert.Equal(registrationId, registration.RegistrationId);
        Assert.Equal("+967733445566", registration.Phone);
        Assert.Equal("Corrected Name", registration.FullNameEnglish);
        Assert.Equal("fixed@example.com", registration.Email);
        Assert.Equal("Pending", registration.Status);
    }

    [Fact]
    public async Task A_duplicate_phone_inside_the_same_course_is_rejected()
    {
        var courseId = await _fixture.Factory.CreateCourseAsync($"دورة تكرار {Guid.NewGuid():N}"[..24]);
        await CreateRegistrationAsync(courseId, "المتدرب الأول");
        var manager = await SignInWithPermissionsAsync(AdminPermissions.ManageRegistrations);

        var response = await manager.PostFormAsync("/Registrations/Create", "/Registrations/Create", new Dictionary<string, string>
        {
            ["FullNameArabic"] = "متدرب مكرر",
            ["FullNameEnglish"] = "Duplicate",
            ["Phone"] = "0771111111",
            ["Gender"] = "Male",
            ["CourseId"] = courseId.ToString()
        });

        response.EnsureSuccessStatusCode();
        Assert.Equal("/Registrations/Create", response.RequestMessage!.RequestUri!.AbsolutePath);

        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("يوجد متدرب آخر مسجل بنفس رقم الهاتف", html);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        Assert.Equal(1, await db.Registrations.CountAsync(r => r.CourseId == courseId));
    }

    [Fact]
    public async Task Course_is_locked_once_the_trainee_is_accepted()
    {
        var courseId = await _fixture.Factory.CreateCourseAsync($"دورة مقفلة {Guid.NewGuid():N}"[..24]);
        var otherCourseId = await _fixture.Factory.CreateCourseAsync($"دورة بديلة {Guid.NewGuid():N}"[..24]);
        var name = UniqueName("متدرب مقبول");
        var registrationId = await CreateRegistrationAsync(courseId, name, status: "Accepted");
        var manager = await SignInWithPermissionsAsync(AdminPermissions.ManageRegistrations);

        var page = WebUtility.HtmlDecode(await manager.GetStringAsync($"/Registrations/Edit/{registrationId}"));
        Assert.Contains("الدورة غير قابلة للتغيير", page);
        Assert.Contains("disabled", page);

        var response = await manager.PostFormAsync($"/Registrations/Edit/{registrationId}", $"/Registrations/Edit/{registrationId}",
            new Dictionary<string, string>
            {
                ["FullNameArabic"] = name,
                ["FullNameEnglish"] = "Accepted Trainee",
                ["Phone"] = "0771111111",
                ["Gender"] = "Male",
                ["CourseId"] = otherCourseId.ToString()
            });

        response.EnsureSuccessStatusCode();
        var registration = await LoadRegistrationAsync(name);
        Assert.Equal(courseId, registration.CourseId);
    }

    [Fact]
    public async Task Inconsistent_academic_chain_is_rejected()
    {
        var courseId = await _fixture.Factory.CreateCourseAsync($"دورة أكاديمية {Guid.NewGuid():N}"[..24]);
        var first = await CreateAcademicChainAsync();
        var second = await CreateAcademicChainAsync();
        var manager = await SignInWithPermissionsAsync(AdminPermissions.ManageRegistrations);

        var response = await manager.PostFormAsync("/Registrations/Create", "/Registrations/Create", new Dictionary<string, string>
        {
            ["FullNameArabic"] = "سلسلة خاطئة",
            ["FullNameEnglish"] = "Broken Chain",
            ["Phone"] = "0772222222",
            ["Gender"] = "Male",
            ["CourseId"] = courseId.ToString(),
            ["UniversityId"] = first.UniversityId.ToString(),
            ["CollegeId"] = second.CollegeId.ToString(),
            ["AcademicSpecializationId"] = second.SpecializationId.ToString(),
            ["AcademicLevel"] = second.Level
        });

        response.EnsureSuccessStatusCode();
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("لا تنتمي إلى الجامعة", html);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        Assert.False(await db.Registrations.AnyAsync(r => r.FullNameArabic == "سلسلة خاطئة"));
    }

    [Fact]
    public async Task Read_only_user_cannot_reach_the_trainee_forms()
    {
        var courseId = await _fixture.Factory.CreateCourseAsync($"دورة قراءة {Guid.NewGuid():N}"[..24]);
        var registrationId = await CreateRegistrationAsync(courseId, "متدرب للقراءة");
        var viewer = await SignInWithPermissionsAsync(AdminPermissions.ViewRegistrations);

        Assert.True(await viewer.IsDeniedAsync("/Registrations/Create"));
        Assert.True(await viewer.IsDeniedAsync($"/Registrations/Edit/{registrationId}"));

        var list = WebUtility.HtmlDecode(await viewer.GetStringAsync("/Registrations"));
        Assert.DoesNotContain("href=\"/Registrations/Create", list);
        Assert.DoesNotContain($"/Registrations/Edit/{registrationId}", list);

        var details = WebUtility.HtmlDecode(await viewer.GetStringAsync($"/Registrations/Details/{registrationId}"));
        Assert.DoesNotContain("تعديل البيانات", details);
    }

    [Fact]
    public async Task Manager_sees_add_and_edit_entry_points()
    {
        var courseId = await _fixture.Factory.CreateCourseAsync($"دورة أزرار {Guid.NewGuid():N}"[..24]);
        var registrationId = await CreateRegistrationAsync(courseId, "متدرب أزرار");
        var manager = await SignInWithPermissionsAsync(AdminPermissions.ManageRegistrations);

        var list = WebUtility.HtmlDecode(await manager.GetStringAsync("/Registrations"));
        Assert.Contains("/Registrations/Create", list);
        Assert.Contains($"/Registrations/Edit/{registrationId}", list);

        var details = WebUtility.HtmlDecode(await manager.GetStringAsync($"/Registrations/Details/{registrationId}"));
        Assert.Contains("/Registrations/Edit/" + registrationId, details);

        var form = WebUtility.HtmlDecode(await manager.GetStringAsync($"/Registrations/Edit/{registrationId}"));
        Assert.Contains("action=\"/Registrations/Edit/" + registrationId + "\"", form);
        Assert.Contains("متدرب أزرار", form);
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private async Task<AppClient> SignInWithPermissionsAsync(params string[] permissionNames)
    {
        var admin = await SignInAdminAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roleName = $"دور متدربين {suffix}";
        var username = $"trainee{suffix}";

        var permissionIds = new List<int>();
        foreach (var name in permissionNames)
            permissionIds.Add(await _fixture.Factory.PermissionIdAsync(name));

        var roleFields = new List<KeyValuePair<string, string>>
        {
            new("RoleName", roleName),
            new("Description", "دور اختبار إدارة المتدربين")
        };
        roleFields.AddRange(permissionIds.Select(id => new KeyValuePair<string, string>("PermissionIds", id.ToString())));
        (await admin.PostFormAsync("/Roles/Create", "/Roles/Create", roleFields)).EnsureSuccessStatusCode();

        var roleId = await _fixture.Factory.RoleIdAsync(roleName);
        var userFields = new List<KeyValuePair<string, string>>
        {
            new("Username", username),
            new("FullName", "مسؤول المتدربين"),
            new("Email", $"{username}@example.com"),
            new("Phone", "0500000000"),
            new("Password", UserPassword),
            new("RoleId", roleId.ToString())
        };
        (await admin.PostFormAsync("/Users/Create", "/Users/Create", userFields)).EnsureSuccessStatusCode();

        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync(username, UserPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    /// <summary>Creates a registration directly, so a test can start from an existing trainee.</summary>
    private async Task<int> CreateRegistrationAsync(int courseId, string name, string status = "Pending")
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var registration = new Registration
        {
            RequestNumber = $"REG-TEST-{Guid.NewGuid():N}"[..24],
            FullName = name,
            FullNameArabic = name,
            FullNameEnglish = "Trainee",
            Gender = "Male",
            Phone = "+967771111111",
            CourseId = courseId,
            Status = status,
            RegistrationDate = DateTime.Now
        };
        db.Registrations.Add(registration);
        await db.SaveChangesAsync();
        return registration.RegistrationId;
    }

    private static string UniqueName(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..8]}";

    private async Task<Registration> LoadRegistrationAsync(string name)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var registration = await db.Registrations.AsNoTracking().FirstOrDefaultAsync(r => r.FullNameArabic == name);
        Assert.NotNull(registration);
        return registration!;
    }

    private sealed record AcademicChain(int UniversityId, string UniversityName, int CollegeId,
        string CollegeName, int SpecializationId, string SpecializationName, string Level);

    private async Task<AcademicChain> CreateAcademicChainAsync()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..6];

        var university = new University { UniversityName = $"جامعة {suffix}" };
        db.Universities.Add(university);
        await db.SaveChangesAsync();

        var college = new College { UniversityId = university.UniversityId, CollegeName = $"كلية {suffix}" };
        db.Colleges.Add(college);
        await db.SaveChangesAsync();

        var specialization = new AcademicSpecialization
        {
            CollegeId = college.CollegeId,
            SpecializationName = $"تخصص {suffix}"
        };
        db.AcademicSpecializations.Add(specialization);
        await db.SaveChangesAsync();

        var level = $"مستوى {suffix}";
        db.AcademicLevelOptions.Add(new AcademicLevelOption
        {
            AcademicSpecializationId = specialization.AcademicSpecializationId,
            LevelName = level,
            SortOrder = 1
        });
        await db.SaveChangesAsync();

        return new AcademicChain(university.UniversityId, university.UniversityName,
            college.CollegeId, college.CollegeName,
            specialization.AcademicSpecializationId, specialization.SpecializationName, level);
    }
}
