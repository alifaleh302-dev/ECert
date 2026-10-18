using System.Text.Json;
using System.Text.Json.Serialization;
using ECert.Data;
using ECert.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.AddService<SiteLayoutDataFilter>();
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

var rawMySqlConn = ResolveMySqlConnectionString(builder.Configuration);
var mysqlConn = NormalizeMySqlConnectionString(rawMySqlConn);

builder.Services.AddDbContext<ECertDbContext>(options =>
    options.UseMySql(mysqlConn, ServerVersion.AutoDetect(mysqlConn)));

// Railway rebuilds containers during each deploy. Persisting Data Protection keys in MySQL
// keeps authentication cookies and antiforgery tokens valid across future deployments.
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ECertDbContext>()
    .SetApplicationName("ECert");

// Railway terminates TLS at the edge; explicitly configure the redirect target to avoid
// startup warnings when the container itself has no HTTPS listener.
builder.Services.AddHttpsRedirection(options => options.HttpsPort = 443);

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddScoped<AuditLogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<SiteLayoutDataFilter>();
builder.Services.AddSingleton<CertificateSecurityService>();
builder.Services.AddSingleton<VerifyRequestGuardService>();
builder.Services.AddSingleton<LoginAttemptGuard>();
builder.Services.AddScoped<CertificateSchemaMigrationService>();
builder.Services.AddScoped<AcademicSchemaMigrationService>();
builder.Services.AddScoped<CourseNameSchemaMigrationService>();
builder.Services.AddScoped<CourseSchemaMigrationService>();
builder.Services.AddScoped<CourseInstructorSchemaMigrationService>();
builder.Services.AddScoped<RegistrationNameSchemaMigrationService>();
builder.Services.AddScoped<RegistrationInvoiceService>();
builder.Services.AddScoped<InvoiceSchemaMigrationService>();
builder.Services.AddScoped<FeeExemptionSchemaMigrationService>();
builder.Services.AddScoped<CertificateDesignService>();
builder.Services.AddScoped<CertificateDesignSchemaMigrationService>();
builder.Services.AddScoped<CashboxSchemaMigrationService>();
builder.Services.AddScoped<PerformanceIndexSchemaMigrationService>();
builder.Services.AddScoped<TurnstileService>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.LogoutPath = "/Auth/Logout";
        options.AccessDeniedPath = "/Home/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.None
            : CookieSecurePolicy.Always;
        options.Events = new CookieAuthenticationEvents
        {
            OnRedirectToLogin = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = 401;
                    context.Response.ContentType = "application/json";
                    return context.Response.WriteAsync(
                        JsonSerializer.Serialize(new { success = false, message = "يجب تسجيل الدخول أولاً" }));
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            },
            OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Path.StartsWithSegments("/api"))
                {
                    context.Response.StatusCode = 403;
                    context.Response.ContentType = "application/json";
                    return context.Response.WriteAsync(
                        JsonSerializer.Serialize(new { success = false, message = "ليس لديك صلاحية الوصول" }));
                }
                context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// Protect anonymous, database-backed endpoints from automated abuse. The partition key is
// the client address resolved by ClientAddressResolver; this is intentionally
// endpoint-specific so normal browsing is not throttled by registration traffic.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public-registration", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientAddressResolver.Resolve(context), _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("public-verification", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientAddressResolver.Resolve(context), _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("admin-login", context =>
        RateLimitPartition.GetFixedWindowLimiter(ClientAddressResolver.Resolve(context), _ =>
            new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "image/svg+xml", "application/json" });
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // The hosting platform terminates TLS at its edge proxy and forwards to the container, so
    // the proxy addresses are not known in advance and cannot be pinned here. Limit the chain
    // to a single hop instead: only the last entry is then trusted, so a caller cannot prepend
    // forged addresses to spoof their origin. ClientAddressResolver reads the rightmost entry
    // for the same reason.
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var bootstrapLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseBootstrap");
        await EnsureSchemaAsync(db, bootstrapLogger);
        await db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS `DataProtectionKeys` (
                `Id` INT NOT NULL AUTO_INCREMENT,
                `FriendlyName` VARCHAR(512) NOT NULL,
                `Xml` LONGTEXT NOT NULL,
                PRIMARY KEY (`Id`)
            ) CHARACTER SET utf8mb4;");
        // Run this before every migration that queries Courses, because the EF model already includes CertificateDesignId.
        var certificateDesignMigration = scope.ServiceProvider.GetRequiredService<CertificateDesignSchemaMigrationService>();
        await certificateDesignMigration.EnsureAsync();
        var courseNameMigration = scope.ServiceProvider.GetRequiredService<CourseNameSchemaMigrationService>();
        await courseNameMigration.EnsureAsync();
        var courseSchemaMigration = scope.ServiceProvider.GetRequiredService<CourseSchemaMigrationService>();
        await courseSchemaMigration.EnsureAsync();
        var courseInstructorMigration = scope.ServiceProvider.GetRequiredService<CourseInstructorSchemaMigrationService>();
        await courseInstructorMigration.EnsureAsync();
        var registrationNameMigration = scope.ServiceProvider.GetRequiredService<RegistrationNameSchemaMigrationService>();
        await registrationNameMigration.EnsureAsync();
        var invoiceMigration = scope.ServiceProvider.GetRequiredService<InvoiceSchemaMigrationService>();
        await invoiceMigration.EnsureAsync();
        var feeExemptionMigration = scope.ServiceProvider.GetRequiredService<FeeExemptionSchemaMigrationService>();
        await feeExemptionMigration.EnsureAsync();
        var certificateMigration = scope.ServiceProvider.GetRequiredService<CertificateSchemaMigrationService>();
        await certificateMigration.EnsureAsync();
        var academicMigration = scope.ServiceProvider.GetRequiredService<AcademicSchemaMigrationService>();
        await academicMigration.EnsureAsync();
        var cashboxMigration = scope.ServiceProvider.GetRequiredService<CashboxSchemaMigrationService>();
        await cashboxMigration.EnsureAsync();
        DbSeeder.Seed(db, bootstrapLogger);
        DbSeeder.SeedHomepageCms(db);
        var performanceIndexMigration = scope.ServiceProvider.GetRequiredService<PerformanceIndexSchemaMigrationService>();
        await performanceIndexMigration.EnsureAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Database initialization failed.");
        Console.Error.WriteLine(ex.ToString());
        throw;
    }
}

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();

    // The platform health probe calls /health over plain HTTP inside the container, so that
    // path must not be redirected to HTTPS.
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/health"),
        branch => branch.UseHttpsRedirection());
}

app.UseResponseCompression();

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.PhysicalPath ?? string.Empty;
        if (path.EndsWith(".js") || path.EndsWith(".css") || path.EndsWith(".woff2"))
            ctx.Context.Response.Headers[HeaderNames.CacheControl] = "public,max-age=604800";
        else if (path.EndsWith(".jpg") || path.EndsWith(".png") || path.EndsWith(".webp") || path.EndsWith(".svg") || path.EndsWith(".ico"))
            ctx.Context.Response.Headers[HeaderNames.CacheControl] = "public,max-age=2592000";
    }
});

if (!app.Environment.IsDevelopment())
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data: https:; style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://challenges.cloudflare.com; font-src 'self' https://cdn.jsdelivr.net; connect-src 'self' https:; frame-src https://challenges.cloudflare.com; frame-ancestors 'none';";
        context.Response.Headers["X-XSS-Protection"] = "1; mode=block";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        await next();
    });
}

app.UseRouting();

// Must run after UseRouting: the [EnableRateLimiting] policies are endpoint metadata, and
// endpoint-scoped policies are only applied once routing has selected an endpoint.
app.UseRateLimiter();

app.UseAuthentication();

// Accounts seeded with a bootstrap password must replace it before using the application.
app.Use(async (context, next) =>
{
    var user = context.User;
    if (user?.Identity?.IsAuthenticated == true &&
        user.HasClaim("MustChangePassword", "true") &&
        !IsPasswordChangeExempt(context.Request.Path))
    {
        context.Response.Redirect("/Auth/ChangePassword");
        return;
    }

    await next();
});

app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString() })
        });
    }
});

app.Run();

static bool IsPasswordChangeExempt(PathString path) =>
    path.StartsWithSegments("/Auth/ChangePassword")
    || path.StartsWithSegments("/Auth/Logout")
    || path.StartsWithSegments("/api/auth")
    || path.StartsWithSegments("/health")
    || path.StartsWithSegments("/css")
    || path.StartsWithSegments("/js")
    || path.StartsWithSegments("/lib")
    || path.StartsWithSegments("/images")
    || path.StartsWithSegments("/favicon");

// EnsureCreated builds the whole schema in one transaction, so a concurrent or interrupted
// first boot can leave a partially created database behind. Retrying it then fails on the
// objects that already exist (e.g. "Duplicate key name") and the app never starts. Only an
// empty database is created; an existing one is used as-is and left to the idempotent
// *SchemaMigrationService upgrades below.
static async Task EnsureSchemaAsync(ECertDbContext db, ILogger logger)
{
    const int maxAttempts = 5;

    for (var attempt = 1; ; attempt++)
    {
        try
        {
            db.Database.EnsureCreated();
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            logger.LogWarning(ex, "Schema creation attempt {Attempt}/{MaxAttempts} failed; retrying.", attempt, maxAttempts);
            await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
        }
    }
}

static string ResolveMySqlConnectionString(IConfiguration configuration)
{
    var candidates = new[]
    {
        configuration.GetConnectionString("MySqlConnection"),
        configuration["ConnectionStrings:MySqlConnection"],
        configuration["ConnectionStrings__MySqlConnection"],
        configuration["MySqlConnection"],
        configuration["DATABASE_URL"]
    };

    var value = candidates.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException(
            "MySQL connection string is missing. Supported keys: " +
            "ConnectionStrings:MySqlConnection, ConnectionStrings__MySqlConnection, MySqlConnection, DATABASE_URL.");
    }

    return value.Trim();
}

static string NormalizeMySqlConnectionString(string value)
{
    if (value.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Host=", StringComparison.OrdinalIgnoreCase))
    {
        return value;
    }

    if (value.StartsWith("mysql://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("mariadb://", StringComparison.OrdinalIgnoreCase))
    {
        return ConvertMySqlUrlToAdoNet(value);
    }

    throw new InvalidOperationException(
        "Unsupported MySQL connection string format. Use ADO.NET format or a mysql:// URL.");
}

static string ConvertMySqlUrlToAdoNet(string url)
{
    var uri = new Uri(url);
    var database = uri.AbsolutePath.Trim('/');
    if (string.IsNullOrWhiteSpace(database))
        throw new InvalidOperationException("MySQL URL must include a database name.");

    var userInfo = uri.UserInfo.Split(':', 2);
    var user = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty;
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;

    if (string.IsNullOrWhiteSpace(user))
        throw new InvalidOperationException("MySQL URL must include a username.");

    var options = ParseQueryString(uri.Query);
    var sslMode = options.TryGetValue("sslmode", out var ssl)
        ? MapSslMode(ssl)
        : options.TryGetValue("ssl-mode", out var sslDash)
            ? MapSslMode(sslDash)
            : "Preferred";

    var parts = new List<string>
    {
        $"Server={uri.Host}",
        $"Port={(uri.IsDefaultPort ? 3306 : uri.Port)}",
        $"Database={database}",
        $"User ID={user}",
        $"Password={password}",
        $"SslMode={sslMode}",
        "AllowPublicKeyRetrieval=True"
    };

    if (options.TryGetValue("charset", out var charset) && !string.IsNullOrWhiteSpace(charset))
        parts.Add($"Character Set={charset}");

    return string.Join(';', parts) + ";";
}

static Dictionary<string, string> ParseQueryString(string query)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(query))
        return result;

    var trimmed = query.TrimStart('?');
    foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
    {
        var pieces = pair.Split('=', 2);
        var key = Uri.UnescapeDataString(pieces[0]);
        var value = pieces.Length > 1 ? Uri.UnescapeDataString(pieces[1]) : string.Empty;
        result[key] = value;
    }

    return result;
}

static string MapSslMode(string value)
{
    return value.Trim().ToLowerInvariant() switch
    {
        "required" => "Required",
        "require" => "Required",
        "preferred" => "Preferred",
        "verifyca" => "VerifyCA",
        "verifyfull" => "VerifyFull",
        "disabled" => "None",
        "disable" => "None",
        "none" => "None",
        _ => "Preferred"
    };
}
