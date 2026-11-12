using ECert.Models;
using ECert.Services;

namespace ECert.Data;

public static class DbSeeder
{
    /// <summary>
    /// Password given to the bootstrap accounts on a brand-new database. Supply
    /// <c>ECERT_BOOTSTRAP_PASSWORD</c> in the environment; otherwise a random password is
    /// generated and written to the log once. Either way every seeded account must change
    /// it at first sign-in.
    /// </summary>
    private const string BootstrapPasswordVariable = "ECERT_BOOTSTRAP_PASSWORD";

    private static string ResolveBootstrapPassword(ILogger? logger)
    {
        var configured = Environment.GetEnvironmentVariable(BootstrapPasswordVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var generated = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12))
            .Replace("+", "A").Replace("/", "B").TrimEnd('=');
        (logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).LogWarning(
            "No {Variable} configured. Seeded accounts use this one-time password: {Password} " +
            "They must change it at first sign-in.",
            BootstrapPasswordVariable, generated);
        Console.Error.WriteLine(
            $"[bootstrap] No {BootstrapPasswordVariable} configured. One-time password for seeded accounts: {generated}");
        return generated;
    }

    /// <summary>
    /// Optional recovery hook. When <c>ECERT_RESET_ADMIN_PASSWORD</c> is set, the <c>admin</c>
    /// account's password is reset to that value and it is flagged to change it at next
    /// sign-in. Intended for the case where a database was seeded before the bootstrap password
    /// was captured; unset the variable afterwards so the value does not stay in the config.
    /// </summary>
    private const string AdminPasswordResetVariable = "ECERT_RESET_ADMIN_PASSWORD";

    private static void ApplyAdminPasswordReset(ECertDbContext db, ILogger? logger)
    {
        var requested = Environment.GetEnvironmentVariable(AdminPasswordResetVariable);
        if (string.IsNullOrWhiteSpace(requested))
            return;

        var admin = db.Users.FirstOrDefault(u => u.Username == "admin");
        if (admin == null)
            return;

        admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(requested);
        admin.MustChangePassword = true;
        admin.IsActive = true;
        db.SaveChanges();
        (logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).LogWarning(
            "{Variable} was set: the admin password was reset and must be changed at next sign-in. " +
            "Remove the variable now.", AdminPasswordResetVariable);
    }

    public static void Seed(ECertDbContext db, ILogger? logger = null)
    {
        SyncPermissionCatalog(db);
        RemoveDefaultNonAdminAccounts(db);

        if (db.Roles.Any())
        {
            EnsureSuperAdminRole(db);
            SeedHomepageCms(db);
            ApplyAdminPasswordReset(db, logger);
            return;
        }

        EnsureSuperAdminRole(db);
        var bootstrapPassword = ResolveBootstrapPassword(logger);

        // Create Super Admin user
        var superAdminRole = db.Roles.First(r => r.RoleName == "SuperAdmin");
        var superAdmin = new User
        {
            Username = "admin",
            FullName = "مدير النظام",
            Email = "admin@training.com",
            Phone = "0500000000",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(bootstrapPassword),
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = DateTime.Now
        };
        db.Users.Add(superAdmin);
        db.SaveChanges();

        db.UserRoles.Add(new UserRole { UserId = superAdmin.UserId, RoleId = superAdminRole.RoleId });
        db.SaveChanges();

        // Seed Categories
        var categories = new List<Category>
        {
            new() { CategoryName = "الرعاية الصحية", Description = "دورات في المجال الصحي والطبي" },
            new() { CategoryName = "تقنية المعلومات", Description = "دورات في البرمجة والتقنية" },
            new() { CategoryName = "الإدارة والأعمال", Description = "دورات إدارية وقيادية" },
            new() { CategoryName = "اللغات", Description = "دورات لغات" },
            new() { CategoryName = "التطوير الذاتي", Description = "دورات تطوير المهارات الشخصية" }
        };
        db.Categories.AddRange(categories);
        db.SaveChanges();

        // Seed Instructors
        var instructors = new List<Instructor>
        {
            new() { FullName = "د. أحمد الشمري", Specialization = "رعاية صحية", Bio = "طبيب متخصص في الرعاية المنزلية بخبرة 15 عاماً", Email = "ahmed@training.com", Phone = "0551111111" },
            new() { FullName = "م. سارة العتيبي", Specialization = "تقنية معلومات", Bio = "مهندسة برمجيات ومدربة معتمدة", Email = "sara@training.com", Phone = "0552222222" },
            new() { FullName = "أ. خالد الدوسري", Specialization = "إدارة أعمال", Bio = "مستشار إداري وخبير في القيادة", Email = "khaled@training.com", Phone = "0553333333" }
        };
        db.Instructors.AddRange(instructors);
        db.SaveChanges();

        // Seed Courses
        var courses = new List<Course>
        {
            new() { CourseName = "التمريض المنزلي", CourseNameArabic = "التمريض المنزلي", CourseNameEnglish = "Home Nursing", ShortDescription = "دورة شاملة في أساسيات التمريض والرعاية المنزلية", FullDescription = "دورة تدريبية شاملة تغطي جميع أساسيات التمريض المنزلي包括 رعاية المرضى وكبار السن", Objectives = "إتقان أساسيات التمريض المنزلي\nالتعامل مع الحالات الطارئة\nرعاية كبار السن", CategoryId = categories[0].CategoryId, InstructorId = instructors[0].InstructorId, StartDate = DateTime.Now.AddDays(7), EndDate = DateTime.Now.AddDays(37), Location = "الرياض - حي النخيل", Price = 500, Status = "OpenForRegistration", IsFeatured = true },
            new() { CourseName = "تطوير تطبيقات الويب", CourseNameArabic = "تطوير تطبيقات الويب", CourseNameEnglish = "Web Application Development", ShortDescription = "تعلم بناء تطبيقات ويب حديثة", FullDescription = "دورة متكاملة في تطوير تطبيقات الويب باستخدام أحدث التقنيات", Objectives = "بناء تطبيقات ويب كاملة\nفهم قواعد البيانات\nالنشر على الإنترنت", CategoryId = categories[1].CategoryId, InstructorId = instructors[1].InstructorId, StartDate = DateTime.Now.AddDays(14), EndDate = DateTime.Now.AddDays(60), Location = "أونلاين", Price = 800, Status = "OpenForRegistration", IsFeatured = true },
            new() { CourseName = "القيادة الإدارية الفعالة", CourseNameArabic = "القيادة الإدارية الفعالة", CourseNameEnglish = "Effective Administrative Leadership", ShortDescription = "برنامج تطوير المهارات القيادية", FullDescription = "برنامج مكثف لتطوير المهارات القيادية والإدارية", Objectives = "تطوير مهارات القيادة\nإدارة الفرق\nاتخاذ القرارات", CategoryId = categories[2].CategoryId, InstructorId = instructors[2].InstructorId, StartDate = DateTime.Now.AddDays(21), EndDate = DateTime.Now.AddDays(42), Location = "جدة - حي الروضة", Price = 1200, Status = "Published", IsFeatured = false }
        };
        db.Courses.AddRange(courses);
        db.SaveChanges();

        // Seed Posts
        var posts = new List<Post>
        {
            new() { Title = "افتتاح فرع جديد في جدة", Content = "يسرنا الإعلان عن افتتاح فرعنا الجديد في مدينة جدة بحي الروضة. الفرع مجهز بأحدث القاعات التدريبية والتقنيات التعليمية.", Author = "إدارة المركز", Status = "Published", PublishedAt = DateTime.Now.AddDays(-3) },
            new() { Title = "خصم 20% على دورات الصيف", Content = "بمناسبة فصل الصيف، نقدم خصم 20% على جميع الدورات المسجلة قبل نهاية الشهر. سارعوا بالتسجيل!", Author = "إدارة المركز", Status = "Published", PublishedAt = DateTime.Now.AddDays(-1) }
        };
        db.Posts.AddRange(posts);
        db.SaveChanges();

        // Seed Phone Countries
        db.PhoneCountries.AddRange(
            new PhoneCountry { CountryName = "اليمن", CountryCode = "+967", MinPhoneLength = 9, MaxPhoneLength = 9, Prefixes = "70,71,73,74,75,77,78,62", IsActive = true },
            new PhoneCountry { CountryName = "السعودية", CountryCode = "+966", MinPhoneLength = 9, MaxPhoneLength = 9, Prefixes = "50,51,52,53,54,55,56,57,58,59", IsActive = true },
            new PhoneCountry { CountryName = "الإمارات", CountryCode = "+971", MinPhoneLength = 9, MaxPhoneLength = 9, Prefixes = "50,52,54,55,56,58", IsActive = true },
            new PhoneCountry { CountryName = "مصر", CountryCode = "+20", MinPhoneLength = 10, MaxPhoneLength = 10, Prefixes = "10,11,12,15", IsActive = true },
            new PhoneCountry { CountryName = "الأردن", CountryCode = "+962", MinPhoneLength = 9, MaxPhoneLength = 9, Prefixes = "77,78,79", IsActive = true }
        );
        db.SaveChanges();

        // Seed Homepage CMS
        SeedHomepageCms(db);
    }

    /// <summary>
    /// Keeps the Permissions table aligned with <see cref="AdminPermissions.Catalog"/>:
    /// adds permissions introduced by a newer build, refreshes descriptions, and drops
    /// permissions the catalog no longer defines (along with their role links). This is what
    /// keeps the role editor offering exactly the permissions some section actually honours.
    /// </summary>
    private static void SyncPermissionCatalog(ECertDbContext db)
    {
        var catalogNames = AdminPermissions.Catalog.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var existing = db.Permissions.ToDictionary(p => p.PermissionName, StringComparer.Ordinal);
        var changed = false;

        foreach (var definition in AdminPermissions.Catalog)
        {
            if (!existing.TryGetValue(definition.Name, out var permission))
            {
                db.Permissions.Add(new Permission { PermissionName = definition.Name, Description = definition.Description });
                changed = true;
            }
            else if (permission.Description != definition.Description)
            {
                permission.Description = definition.Description;
                changed = true;
            }
        }

        var obsolete = existing.Values.Where(p => !catalogNames.Contains(p.PermissionName)).ToList();
        if (obsolete.Count > 0)
        {
            var obsoleteIds = obsolete.Select(p => p.PermissionId).ToList();
            db.RolePermissions.RemoveRange(db.RolePermissions.Where(rp => obsoleteIds.Contains(rp.PermissionId)));
            db.Permissions.RemoveRange(obsolete);
            changed = true;
        }

        if (changed) db.SaveChanges();
    }

    /// <summary>
    /// The SuperAdmin role is the only built-in role. It is created on demand (so databases
    /// seeded by older builds are upgraded in place) and always holds every permission.
    /// </summary>
    private static void EnsureSuperAdminRole(ECertDbContext db)
    {
        var role = db.Roles.FirstOrDefault(r => r.RoleName == "SuperAdmin");
        if (role == null)
        {
            role = new Role { RoleName = "SuperAdmin", Description = "مدير النظام - تحكم كامل", IsSystem = true };
            db.Roles.Add(role);
            db.SaveChanges();
        }

        var granted = db.RolePermissions.Where(rp => rp.RoleId == role.RoleId).Select(rp => rp.PermissionId).ToHashSet();
        var missing = db.Permissions.Where(p => !granted.Contains(p.PermissionId))
            .Select(p => new RolePermission { RoleId = role.RoleId, PermissionId = p.PermissionId })
            .ToList();

        if (missing.Count > 0)
        {
            db.RolePermissions.AddRange(missing);
            db.SaveChanges();
        }
    }

    /// <summary>
    /// The system ships with a single account (admin) and a single built-in role
    /// (SuperAdmin). This removes the demo accounts and built-in roles that older builds
    /// seeded automatically (Admin/Media/Finance roles; manager/media/finance users).
    /// Roles and users an administrator created themselves are left untouched.
    /// </summary>
    private static void RemoveDefaultNonAdminAccounts(ECertDbContext db)
    {
        // Both tables are tiny and this runs once at startup, so the name matching is done
        // in memory: translating a string-array Contains through EF is fragile here.
        // Only the exact accounts older builds seeded are removed — matched on the seeded
        // email/domain too, so an administrator's own "manager" user or "Admin" role survives.
        var legacyUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "manager", "media", "finance" };
        var legacyUsers = db.Users.ToList()
            .Where(u => legacyUsernames.Contains(u.Username)
                        && (u.Email ?? string.Empty).EndsWith("@training.com", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (legacyUsers.Count > 0)
        {
            var legacyUserIds = legacyUsers.Select(u => u.UserId).ToList();
            db.UserRoles.RemoveRange(db.UserRoles.Where(ur => legacyUserIds.Contains(ur.UserId)));
            db.Users.RemoveRange(legacyUsers);
            db.SaveChanges();
        }

        var legacyRoleNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Admin", "Media", "Finance" };
        var legacyRoles = db.Roles.ToList()
            .Where(r => r.IsSystem && legacyRoleNames.Contains(r.RoleName))
            .ToList();
        if (legacyRoles.Count > 0)
        {
            var legacyRoleIds = legacyRoles.Select(r => r.RoleId).ToList();
            db.RolePermissions.RemoveRange(db.RolePermissions.Where(rp => legacyRoleIds.Contains(rp.RoleId)));
            db.UserRoles.RemoveRange(db.UserRoles.Where(ur => legacyRoleIds.Contains(ur.RoleId)));
            db.Roles.RemoveRange(legacyRoles);
            db.SaveChanges();
        }
    }

    public static void SeedHomepageCms(ECertDbContext db)
    {
        EnsureSiteSetting(db, "SiteName", "مركز التدريب", "General");
        EnsureSiteSetting(db, "LogoUrl", string.Empty, "General");
        EnsureSiteSetting(db, "FaviconUrl", string.Empty, "General");
        EnsureSiteSetting(db, "CopyrightText", string.Empty, "General");
        EnsureSiteSetting(db, "MetaDescription", "مركز تدريبي معتمد يقدم دورات احترافية في مختلف المجالات", "SEO");
        EnsureSiteSetting(db, "SeoKeywords", "تدريب,دورات,شهادات,تعليم", "SEO");
        EnsureSiteSetting(db, "AboutTitle", "من نحن", "Content");
        EnsureSiteSetting(db, "AboutSummary", "نقدم برامج تدريبية احترافية وشهادات موثقة تساعد المتدربين على بناء مستقبل مهني أقوى.", "Content");
        EnsureSiteSetting(db, "AboutContent", "نحن جهة تدريبية تهتم بتقديم برامج عملية ومعتمدة في مجالات متنوعة، مع تجربة رقمية متكاملة تشمل التسجيل وإصدار الشهادات والتحقق منها إلكترونياً.", "Content");
        EnsureSiteSetting(db, "FooterEnabled", "true", "Layout");
        EnsureSiteSetting(db, "FooterShowQuickLinks", "true", "Layout");
        EnsureSiteSetting(db, "FooterShowContact", "true", "Layout");
        EnsureSiteSetting(db, "FooterShowAboutLinks", "true", "Layout");
        EnsureSiteSetting(db, "FooterShowSocial", "true", "Layout");

        // Financial system settings (currency + refund approval ceiling). The symbol is display-only;
        // the system never performs currency conversion.
        EnsureSiteSetting(db, "CurrencyName", "ريال يمني", "Finance");
        EnsureSiteSetting(db, "CurrencySymbol", "ر.ي", "Finance");
        // Refunds at or below this ceiling apply immediately; a larger one waits for a SuperAdmin.
        EnsureSiteSetting(db, "RefundApprovalThreshold", "500", "Finance");
        BackfillBlankRefundThreshold(db);

        if (!db.ExpenseCategories.Any())
        {
            var defaults = new[] { "رواتب وأجور", "إيجار", "مستلزمات وقرطاسية", "خدمات ومرافق", "تسويق وإعلان", "صيانة", "مصروفات أخرى" };
            var order = 0;
            foreach (var name in defaults)
                db.ExpenseCategories.Add(new ExpenseCategory { Name = name, SortOrder = order++, IsActive = true });
            db.SaveChanges();
        }

        if (!db.HeroSlides.Any())
        {
            db.HeroSlides.Add(new HeroSlide { Title = "طور مهاراتك مع أفضل المدربين", Description = "نقدم دورات تدريبية معتمدة في مختلف المجالات مع شهادات معتمدة", ImageUrl = "", Button1Text = "استكشف الدورات", Button1Url = "/Courses", Button2Text = "تحقق من شهادة", Button2Url = "/Certificates/Verify", SortOrder = 1, IsActive = true });
        }

        if (!db.HeroAnimatedTexts.Any())
        {
            db.HeroAnimatedTexts.AddRange(
                new HeroAnimatedText { Text = "طور مهاراتك", SortOrder = 1, IsActive = true },
                new HeroAnimatedText { Text = "احصل على شهادة معتمدة", SortOrder = 2, IsActive = true },
                new HeroAnimatedText { Text = "ابدأ مستقبلك المهني", SortOrder = 3, IsActive = true }
            );
        }

        if (!db.SocialLinks.Any())
        {
            db.SocialLinks.AddRange(
                new SocialLink { PlatformName = "Facebook", IconClass = "bi-facebook", Url = "#", SortOrder = 1, IsActive = true },
                new SocialLink { PlatformName = "X", IconClass = "bi-twitter-x", Url = "#", SortOrder = 2, IsActive = true },
                new SocialLink { PlatformName = "Instagram", IconClass = "bi-instagram", Url = "#", SortOrder = 3, IsActive = true },
                new SocialLink { PlatformName = "LinkedIn", IconClass = "bi-linkedin", Url = "#", SortOrder = 4, IsActive = true },
                new SocialLink { PlatformName = "YouTube", IconClass = "bi-youtube", Url = "#", SortOrder = 5, IsActive = true }
            );
        }

        if (!db.ContactInfos.Any())
        {
            db.ContactInfos.Add(new ContactInfo
            {
                Phone = "01-234567",
                Mobile = "771234567",
                Email = "info@training.com",
                Website = "www.training.com",
                Address = "صنعاء - اليمن",
                WorkingHours = "السبت - الخميس: 8 صباحاً - 4 عصراً",
                ShowPhone = true,
                ShowMobile = true,
                ShowEmail = true,
                ShowWebsite = false,
                ShowAddress = true,
                ShowWorkingHours = true
            });
        }

        if (!db.StatCards.Any())
        {
            db.StatCards.AddRange(
                new StatCard { Label = "دورة تدريبية", IconClass = "bi-journal-bookmark", Color = "#2563eb", SortOrder = 1, IsActive = true, IsDynamic = true, DynamicSource = "Courses" },
                new StatCard { Label = "مدرب معتمد", IconClass = "bi-person-badge", Color = "#7c3aed", SortOrder = 2, IsActive = true, IsDynamic = true, DynamicSource = "Instructors" },
                new StatCard { Label = "متدرب", IconClass = "bi-people", Color = "#059669", SortOrder = 3, IsActive = true, IsDynamic = true, DynamicSource = "Trainees" },
                new StatCard { Label = "شهادة صادرة", IconClass = "bi-award", Color = "#d97706", SortOrder = 4, IsActive = true, IsDynamic = true, DynamicSource = "Certificates" }
            );
        }

        if (!db.HomepageSections.Any())
        {
            db.HomepageSections.AddRange(
                new HomepageSection { SectionKey = "Categories", SectionName = "التصنيفات", IsVisible = true, SortOrder = 1 },
                new HomepageSection { SectionKey = "Courses", SectionName = "الدورات", IsVisible = true, SortOrder = 2 },
                new HomepageSection { SectionKey = "Stats", SectionName = "الإحصائيات", IsVisible = true, SortOrder = 3 },
                new HomepageSection { SectionKey = "News", SectionName = "الأخبار", IsVisible = true, SortOrder = 4 },
                new HomepageSection { SectionKey = "Instructors", SectionName = "المدربون", IsVisible = true, SortOrder = 5 },
                new HomepageSection { SectionKey = "Testimonials", SectionName = "آراء المتدربين", IsVisible = false, SortOrder = 6 },
                new HomepageSection { SectionKey = "Partners", SectionName = "الشركاء", IsVisible = false, SortOrder = 7 },
                new HomepageSection { SectionKey = "CTA", SectionName = "دعوة للتسجيل", IsVisible = true, SortOrder = 8 }
            );
        }

        if (!db.ThemeSettings.Any())
        {
            db.ThemeSettings.Add(new ThemeSetting
            {
                PrimaryColor = "#2563eb",
                SecondaryColor = "#f59e0b",
                ButtonColor = "#2563eb",
                NavbarColor = "#0f172a",
                FooterColor = "#111827"
            });
        }

        db.SaveChanges();
    }

    private static void EnsureSiteSetting(ECertDbContext db, string key, string value, string category)
    {
        if (db.SiteSettings.Any(s => s.Key == key)) return;
        db.SiteSettings.Add(new SiteSetting { Key = key, Value = value, Category = category });
    }

    /// <summary>
    /// Seeds the 500 default onto an already-provisioned database whose refund ceiling was left
    /// blank (an empty ceiling meant "no approval needed"). Only a blank value is filled, so an
    /// administrator who deliberately chose a different ceiling is never overridden.
    /// </summary>
    private static void BackfillBlankRefundThreshold(ECertDbContext db)
    {
        var setting = db.SiteSettings.FirstOrDefault(s => s.Key == "RefundApprovalThreshold");
        if (setting != null && string.IsNullOrWhiteSpace(setting.Value))
            setting.Value = "500";
    }
}
