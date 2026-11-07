using ECert.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;

namespace ECert.Tests.Infrastructure;

/// <summary>
/// Boots the real application (its real <c>Program</c>, middleware, controllers and MySQL schema)
/// against a throwaway database created for the test run. Nothing is mocked: the tests exercise the
/// same login, authorization and role-editing paths the browser hits.
/// </summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>, IAsyncDisposable
{
    /// <summary>Password the tests install on the seeded <c>admin</c> account.</summary>
    public const string AdminPassword = "Admin@12345";

    private readonly string _databaseName = "ecert_test_" + Guid.NewGuid().ToString("N")[..12];
    private readonly MySqlConnectionStringBuilder _serverBuilder;

    public TestAppFactory()
    {
        _serverBuilder = new MySqlConnectionStringBuilder
        {
            Server = Environment.GetEnvironmentVariable("ECERT_TEST_MYSQL_SERVER") ?? "127.0.0.1",
            Port = uint.TryParse(Environment.GetEnvironmentVariable("ECERT_TEST_MYSQL_PORT"), out var port) ? port : 3306,
            UserID = Environment.GetEnvironmentVariable("ECERT_TEST_MYSQL_USER") ?? "ecert",
            Password = Environment.GetEnvironmentVariable("ECERT_TEST_MYSQL_PASSWORD") ?? "ecert_pass",
            SslMode = MySqlSslMode.None,
            AllowPublicKeyRetrieval = true
        };

        CreateDatabase();
    }

    public string ConnectionString
    {
        get
        {
            var builder = new MySqlConnectionStringBuilder(_serverBuilder.ConnectionString)
            {
                Database = _databaseName
            };
            return builder.ConnectionString;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Development keeps cookie SecurePolicy=None so the test client can authenticate over HTTP.
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:MySqlConnection", ConnectionString);
    }

    public async Task<int> PermissionIdAsync(string permissionName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        return await db.Permissions.Where(p => p.PermissionName == permissionName).Select(p => p.PermissionId).FirstAsync();
    }

    public async Task<int> RoleIdAsync(string roleName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        return await db.Roles.Where(r => r.RoleName == roleName).Select(r => r.RoleId).FirstAsync();
    }

    public async Task<bool> RoleExistsAsync(string roleName)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        return await db.Roles.AnyAsync(r => r.RoleName == roleName);
    }

    public async Task<int> UserIdAsync(string username)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        return await db.Users.Where(u => u.Username == username).Select(u => u.UserId).FirstAsync();
    }

    /// <summary>
    /// Installs a known password on the seeded admin account so tests can sign in. The account is
    /// created by the seeder with a random bootstrap password and a forced password change, both of
    /// which would otherwise require the change-password dance before any admin action.
    /// </summary>
    public async Task EnsureAdminPasswordAsync(string password = AdminPassword)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var admin = await db.Users.FirstAsync(u => u.Username == "admin");
        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password);
        admin.MustChangePassword = false;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Creates a registration and its invoice directly, so a payment test has a real invoice to
    /// target without driving the whole accept flow. Returns the new invoice id.
    /// </summary>
    public async Task<int> CreateInvoiceAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var course = await db.Courses.FirstAsync();
        var registration = new ECert.Models.Registration
        {
            RequestNumber = $"REG-TEST-{Guid.NewGuid():N}"[..20],
            FullName = "متدرب اختبار",
            FullNameArabic = "متدرب اختبار",
            FullNameEnglish = "Test Trainee",
            Gender = "Male",
            Phone = "+967700000000",
            CourseId = course.CourseId,
            Status = "Accepted",
            RegistrationDate = DateTime.Now
        };
        db.Registrations.Add(registration);
        await db.SaveChangesAsync();

        var invoice = new ECert.Models.Invoice
        {
            InvoiceNumber = $"INV-TEST-{Guid.NewGuid():N}"[..20],
            RegistrationId = registration.RegistrationId,
            TraineeName = registration.FullName,
            TraineePhone = registration.Phone,
            CourseName = course.CourseName,
            OriginalAmount = 500m,
            TotalAmount = 500m,
            PaidAmount = 0m,
            Status = "Unpaid",
            CreatedAt = DateTime.Now
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice.InvoiceId;
    }

    /// <summary>Reads an invoice's paid amount and status, for asserting payment side effects.</summary>
    public async Task<(decimal PaidAmount, string Status)> GetInvoiceAsync(int invoiceId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var invoice = await db.Invoices.FirstAsync(i => i.InvoiceId == invoiceId);
        return (invoice.PaidAmount, invoice.Status);
    }

    /// <summary>Runs an arbitrary query against the throwaway database on a fresh scope.</summary>
    public async Task<T> WithDbAsync<T>(Func<ECertDbContext, Task<T>> query)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        return await query(db);
    }

    public Task<(int Id, string Name)> FirstInstructorAsync() =>
        WithDbAsync(async db =>
        {
            var instructor = await db.Instructors.OrderBy(i => i.InstructorId).FirstAsync();
            return (instructor.InstructorId, instructor.FullName);
        });

    public Task<(int Id, string Name)> FirstCategoryAsync() =>
        WithDbAsync(async db =>
        {
            var category = await db.Categories.OrderBy(c => c.CategoryId).FirstAsync();
            return (category.CategoryId, category.CategoryName);
        });

    public Task<int> CountLegacyCertificatesAsync() =>
        WithDbAsync(db => db.Certificates.CountAsync(c => c.Source == "Legacy"));

    public Task<ECert.Models.Certificate?> FindCertificateAsync(string certificateNumber) =>
        WithDbAsync(db => db.Certificates.AsNoTracking().FirstOrDefaultAsync(c => c.CertificateNumber == certificateNumber));

    public Task<int> CountCoursesNamedAsync(string courseName) =>
        WithDbAsync(db => db.Courses.CountAsync(c => c.CourseNameArabic == courseName));

    public Task<int> CountCoursesAsync() => WithDbAsync(db => db.Courses.CountAsync());

    public Task<int> CourseIdByNameAsync(string courseName) =>
        WithDbAsync(db => db.Courses.Where(c => c.CourseNameArabic == courseName).Select(c => c.CourseId).FirstAsync());

    public Task<ECert.Models.Course?> FindCourseByNameAsync(string courseName) =>
        WithDbAsync(db => db.Courses.AsNoTracking().FirstOrDefaultAsync(c => c.CourseNameArabic == courseName));

    public Task<List<int>> CourseInstructorIdsAsync(int courseId) =>
        WithDbAsync(db => db.CourseInstructors.Where(ci => ci.CourseId == courseId).OrderBy(ci => ci.SortOrder).Select(ci => ci.InstructorId).ToListAsync());

    public Task<List<(int Id, string Name)>> InstructorsAsync() =>
        WithDbAsync(async db => await db.Instructors.OrderBy(i => i.InstructorId)
            .Select(i => new ValueTuple<int, string>(i.InstructorId, i.FullName)).ToListAsync());

    public Task<int> CategoryIdByNameAsync(string categoryName) =>
        WithDbAsync(db => db.Categories.Where(c => c.CategoryName == categoryName).Select(c => c.CategoryId).FirstAsync());

    public Task<int> FirstOpenCourseIdAsync() =>
        WithDbAsync(db => db.Courses
            .Where(c => c.Status == "OpenForRegistration" && !c.RequiresAcademicDetails)
            .Select(c => c.CourseId).FirstAsync());

    public Task<ECert.Models.Registration?> FindRegistrationByPhoneAsync(string phone) =>
        WithDbAsync(db => db.Registrations.AsNoTracking().FirstOrDefaultAsync(r => r.Phone == phone));

    public Task<int> ActiveCountryCountAsync() =>
        WithDbAsync(db => db.PhoneCountries.CountAsync(c => c.IsActive));

    private void CreateDatabase()
    {
        using var connection = new MySqlConnection(_serverBuilder.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE `{_databaseName}` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;";
        command.ExecuteNonQuery();
    }

    private void DropDatabase()
    {
        try
        {
            MySqlConnection.ClearAllPools();
            using var connection = new MySqlConnection(_serverBuilder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS `{_databaseName}`;";
            command.ExecuteNonQuery();
        }
        catch
        {
            // A leftover throwaway database is harmless; never fail the run over cleanup.
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        DropDatabase();
        GC.SuppressFinalize(this);
    }
}
