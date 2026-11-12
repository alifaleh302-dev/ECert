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

    private readonly string _databaseName;
    private readonly MySqlConnectionStringBuilder _serverBuilder;
    private bool _ownsDatabase;

    /// <param name="existingDatabaseName">
    /// When supplied, the factory attaches to that database instead of creating a throwaway one.
    /// Used to boot a second host against a schema left by a previous host.
    /// </param>
    public TestAppFactory(string? existingDatabaseName = null)
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

        _databaseName = existingDatabaseName ?? "ecert_test_" + Guid.NewGuid().ToString("N")[..12];
        _ownsDatabase = existingDatabaseName is null;
        if (_ownsDatabase)
            CreateDatabase();
    }

    public string DatabaseName => _databaseName;

    /// <summary>Relinquishes ownership so disposal leaves the database for a follow-up host.</summary>
    public void Detach() => _ownsDatabase = false;

    /// <summary>Drops a database by name using the default test server settings.</summary>
    public static void DropDatabaseByName(string databaseName)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = Environment.GetEnvironmentVariable("ECERT_TEST_MYSQL_SERVER") ?? "127.0.0.1",
            Port = uint.TryParse(Environment.GetEnvironmentVariable("ECERT_TEST_MYSQL_PORT"), out var port) ? port : 3306,
            UserID = Environment.GetEnvironmentVariable("ECERT_TEST_MYSQL_USER") ?? "ecert",
            Password = Environment.GetEnvironmentVariable("ECERT_TEST_MYSQL_PASSWORD") ?? "ecert_pass",
            SslMode = MySqlSslMode.None,
            AllowPublicKeyRetrieval = true
        };
        try
        {
            MySqlConnection.ClearAllPools();
            using var connection = new MySqlConnection(builder.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS `{databaseName}`;";
            command.ExecuteNonQuery();
        }
        catch
        {
            // Cleanup is best-effort; a leftover throwaway database must not fail the run.
        }
    }

    /// <summary>Runs arbitrary SQL against this factory's database, for shaping a legacy schema.</summary>
    public async Task ExecuteRawAsync(string sql)
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>True when the given column exists on the table in this factory's database.</summary>
    public async Task<bool> ColumnExistsAsync(string table, string column) =>
        await WithDbAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c;";
            var t = command.CreateParameter(); t.ParameterName = "@t"; t.Value = table; command.Parameters.Add(t);
            var c = command.CreateParameter(); c.ParameterName = "@c"; c.Value = column; command.Parameters.Add(c);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        });

    /// <summary>True when the given table exists in this factory's database.</summary>
    public async Task<bool> TableExistsAsync(string table) =>
        await WithDbAsync(async db =>
        {
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t;";
            var t = command.CreateParameter(); t.ParameterName = "@t"; t.Value = table; command.Parameters.Add(t);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        });

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

    /// <summary>The name of the (single) role bound to a user, for form round-trips.</summary>
    public async Task<string> RoleNameOfUserAsync(string username)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        return await db.UserRoles.Where(ur => ur.User!.Username == username)
            .Select(ur => ur.Role!.RoleName).FirstAsync();
    }

    /// <summary>
    /// Seeds two hero slides whose URL fields carry a stored-XSS payload, so a rendering test can
    /// assert the homepage neutralises them instead of emitting javascript: into an href. Two
    /// slides are needed because the background-image style attribute only renders in slider mode.
    /// </summary>
    public async Task SeedHostileHeroSlideAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        for (var i = 0; i < 2; i++)
        {
            db.HeroSlides.Add(new ECert.Models.HeroSlide
            {
                Title = "شريحة اختبار",
                ImageUrl = "/uploads/x.png');background:red/*",
                Button1Text = "اضغط",
                Button1Url = "javascript:alert(1)",
                SortOrder = 100 + i,
                IsActive = true
            });
        }
        db.SocialLinks.Add(new ECert.Models.SocialLink
        {
            PlatformName = "Test",
            IconClass = "bi bi-link",
            Url = "javascript:alert(2)",
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Removes the rows created by <see cref="SeedHostileHeroSlideAsync"/>.</summary>
    public async Task RemoveHostileHeroSlideAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        await db.HeroSlides.Where(s => s.Title == "شريحة اختبار").ExecuteDeleteAsync();
        await db.SocialLinks.Where(s => s.PlatformName == "Test" && s.Url == "javascript:alert(2)").ExecuteDeleteAsync();
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

    /// <summary>Creates a distinct course so a test can confine a user to it (or keep them out).</summary>
    public async Task<int> CreateCourseAsync(string name)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var course = new ECert.Models.Course
        {
            CourseName = name,
            CourseNameArabic = name,
            CourseNameEnglish = name,
            CategoryId = await db.Categories.Select(c => c.CategoryId).FirstAsync(),
            InstructorId = await db.Instructors.Select(i => i.InstructorId).FirstAsync(),
            Price = 500m,
            Status = "OpenForRegistration",
            CreatedAt = DateTime.Now
        };
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return course.CourseId;
    }

    /// <summary>Creates an accepted registration and invoice in a specific course. Returns the invoice id.</summary>
    public async Task<int> CreateInvoiceForCourseAsync(int courseId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var course = await db.Courses.FirstAsync(c => c.CourseId == courseId);
        var registration = new ECert.Models.Registration
        {
            RequestNumber = $"REG-TEST-{Guid.NewGuid():N}"[..20],
            FullName = "متدرب اختبار",
            FullNameArabic = "متدرب اختبار",
            FullNameEnglish = "Test Trainee",
            Gender = "Male",
            Phone = $"+9677{Random.Shared.Next(10000000, 99999999)}",
            CourseId = courseId,
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

    /// <summary>Adds an active payment directly, so an invoice can start with a non-zero balance.</summary>
    public async Task<int> AddPaymentAsync(int invoiceId, decimal amount, string method = "Cash")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var invoice = await db.Invoices.FirstAsync(i => i.InvoiceId == invoiceId);
        var payment = new ECert.Models.Payment
        {
            InvoiceId = invoiceId,
            Amount = amount,
            PaymentMethod = method,
            PaymentDate = DateTime.Now,
            RecordedBy = "seed",
            CreatedAt = DateTime.Now
        };
        db.Payments.Add(payment);
        invoice.PaidAmount += amount;
        invoice.Status = invoice.PaidAmount >= invoice.TotalAmount ? "Paid" : "PartiallyPaid";
        await db.SaveChangesAsync();
        return payment.PaymentId;
    }

    /// <summary>Assigns a user to the given courses, replacing any existing assignment.</summary>
    public async Task AssignCoursesAsync(string username, params int[] courseIds)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var user = await db.Users.Include(u => u.CourseAssignments).FirstAsync(u => u.Username == username);
        db.UserCourses.RemoveRange(user.CourseAssignments);
        foreach (var courseId in courseIds)
            db.UserCourses.Add(new ECert.Models.UserCourse { UserId = user.UserId, CourseId = courseId });
        await db.SaveChangesAsync();
    }

    /// <summary>Assigns a user to the given categories, replacing any existing category grants.</summary>
    public async Task AssignCategoriesAsync(string username, params int[] categoryIds)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var user = await db.Users.Include(u => u.CategoryAssignments).FirstAsync(u => u.Username == username);
        db.UserCategories.RemoveRange(user.CategoryAssignments);
        foreach (var categoryId in categoryIds)
            db.UserCategories.Add(new ECert.Models.UserCategory { UserId = user.UserId, CategoryId = categoryId });
        await db.SaveChangesAsync();
    }

    /// <summary>Creates a category so a test can grant a whole category of courses.</summary>
    public async Task<int> CreateCategoryAsync(string name)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var category = new ECert.Models.Category { CategoryName = name };
        db.Categories.Add(category);
        await db.SaveChangesAsync();
        return category.CategoryId;
    }

    /// <summary>Creates a course inside a specific category, for category-scope assertions.</summary>
    public async Task<int> CreateCourseInCategoryAsync(string name, int categoryId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var course = new ECert.Models.Course
        {
            CourseName = name,
            CourseNameArabic = name,
            CourseNameEnglish = name,
            CategoryId = categoryId,
            InstructorId = await db.Instructors.Select(i => i.InstructorId).FirstAsync(),
            Price = 500m,
            Status = "OpenForRegistration",
            CreatedAt = DateTime.Now
        };
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return course.CourseId;
    }

    /// <summary>The category id of a course, for asserting category membership.</summary>
    public Task<int> CategoryIdOfCourseAsync(int courseId) =>
        WithDbAsync(db => db.Courses.Where(c => c.CourseId == courseId).Select(c => c.CategoryId).FirstAsync());

    /// <summary>Moves a course into a different category, to prove category grants track new members.</summary>
    public async Task MoveCourseToCategoryAsync(int courseId, int categoryId)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var course = await db.Courses.FirstAsync(c => c.CourseId == courseId);
        course.CategoryId = categoryId;
        await db.SaveChangesAsync();
    }

    /// <summary>The course id an invoice belongs to, for asserting isolation.</summary>
    public Task<int> CourseIdOfInvoiceAsync(int invoiceId) =>
        WithDbAsync(db => db.Invoices.Where(i => i.InvoiceId == invoiceId)
            .Select(i => i.Registration!.CourseId).FirstAsync());

    /// <summary>The invoice number of a specific invoice, for asserting it appears in a listing.</summary>
    public Task<string> InvoiceNumberOfAsync(int invoiceId) =>
        WithDbAsync(db => db.Invoices.Where(i => i.InvoiceId == invoiceId).Select(i => i.InvoiceNumber).FirstAsync());

    /// <summary>The invoice number of the most recent invoice in a course, for isolation assertions.</summary>
    public Task<string> InvoiceNumberForCourseAsync(int courseId) =>
        WithDbAsync(db => db.Invoices.Where(i => i.Registration!.CourseId == courseId)
            .OrderByDescending(i => i.InvoiceId).Select(i => i.InvoiceNumber).FirstAsync());

    /// <summary>An invoice's current status, for asserting that an action did or did not change it.</summary>
    public Task<string> InvoiceStatusAsync(int invoiceId) =>
        WithDbAsync(async db => await db.Invoices.Where(i => i.InvoiceId == invoiceId).Select(i => i.Status).SingleAsync());

    /// <summary>The signed custody balance of a user (sum of their custody entries).</summary>
    public Task<decimal> CustodyBalanceAsync(int userId) =>
        WithDbAsync(async db => await db.CustodyEntries.Where(e => e.UserId == userId)
            .SumAsync(e => (decimal?)e.Amount) ?? 0m);

    /// <summary>How many custody entries of a given type a user has.</summary>
    public Task<int> CustodyEntryCountAsync(int userId, string entryType) =>
        WithDbAsync(db => db.CustodyEntries.CountAsync(e => e.UserId == userId && e.EntryType == entryType));

    /// <summary>Total amount refunded on an invoice.</summary>
    public Task<decimal> RefundedTotalAsync(int invoiceId) =>
        WithDbAsync(async db => await db.Refunds.Where(r => r.InvoiceId == invoiceId)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m);

    /// <summary>Counts financial audit rows of a given action type.</summary>
    public Task<int> FinancialAuditCountAsync(string actionType) =>
        WithDbAsync(db => db.FinancialAuditEntries.CountAsync(e => e.ActionType == actionType));

    /// <summary>Latest custody-handover status for a user (or null when they have none).</summary>
    public Task<string?> LatestHandoverStatusAsync(int userId) =>
        WithDbAsync(async db => await db.CustodyHandovers.Where(h => h.UserId == userId)
            .OrderByDescending(h => h.CustodyHandoverId).Select(h => h.Status).FirstOrDefaultAsync());

    /// <summary>The user id of the seeded admin account.</summary>
    public Task<int> AdminUserIdAsync() => UserIdAsync("admin");

    /// <summary>Sets the DueDate of an invoice, so receivables ageing can be asserted.</summary>
    public async Task SetInvoiceDueDateAsync(int invoiceId, DateTime dueDate)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var invoice = await db.Invoices.FirstAsync(i => i.InvoiceId == invoiceId);
        invoice.DueDate = dueDate;
        await db.SaveChangesAsync();
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
        if (_ownsDatabase)
            DropDatabase();
        GC.SuppressFinalize(this);
    }
}
