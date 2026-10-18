using ECert.Models;

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
        EnsureCashboxPermissions(db);
        if (db.Roles.Any())
        {
            EnsureCertificateDesignPermission(db);
            SeedHomepageCms(db);
            ApplyAdminPasswordReset(db, logger);
            return;
        }

        var bootstrapPassword = ResolveBootstrapPassword(logger);

        // Create Permissions
        var permissions = new List<Permission>
        {
            new() { PermissionName = "manage-registrations", Description = "إدارة التسجيلات" },
            new() { PermissionName = "manage-courses", Description = "إدارة الدورات" },
            new() { PermissionName = "manage-instructors", Description = "إدارة المدربين" },
            new() { PermissionName = "manage-finance", Description = "إدارة المالية" },
            new() { PermissionName = "view-invoices", Description = "مشاهدة الفواتير" },
            new() { PermissionName = "manage-payments", Description = "إدارة الدفعات" },
            new() { PermissionName = "view-reports", Description = "مشاهدة التقارير" },
            new() { PermissionName = "issue-certificates", Description = "إصدار الشهادات" },
            new() { PermissionName = "manage-certificate-designs", Description = "إدارة تصميم الشهادات" },
            new() { PermissionName = "manage-posts", Description = "إدارة المنشورات" },
            new() { PermissionName = "manage-news", Description = "إدارة الأخبار" },
            new() { PermissionName = "manage-users", Description = "إدارة المستخدمين" },
            new() { PermissionName = "manage-roles", Description = "إدارة الأدوار والصلاحيات" },
            new() { PermissionName = "view-audit-log", Description = "مشاهدة سجل العمليات" },
            new() { PermissionName = "manage-categories", Description = "إدارة الفئات" },
            new() { PermissionName = "manage-notifications", Description = "إدارة الإشعارات" }
        };
        db.Permissions.AddRange(permissions);
        db.SaveChanges();

        // Create Roles
        var superAdminRole = new Role { RoleName = "SuperAdmin", Description = "سوبر أدمن - تحكم كامل", IsSystem = true };
        var adminRole = new Role { RoleName = "Admin", Description = "مستخدم إداري", IsSystem = true };
        var mediaRole = new Role { RoleName = "Media", Description = "مستخدم إعلامي", IsSystem = true };
        var financeRole = new Role { RoleName = "Finance", Description = "مستخدم مالي", IsSystem = true };

        db.Roles.AddRange(superAdminRole, adminRole, mediaRole, financeRole);
        db.SaveChanges();

        // Assign ALL permissions to SuperAdmin
        foreach (var p in permissions)
            db.RolePermissions.Add(new RolePermission { RoleId = superAdminRole.RoleId, PermissionId = p.PermissionId });

        // Admin permissions (no finance, no users, no roles)
        var adminPerms = new[] { "manage-registrations", "manage-courses", "manage-instructors", "issue-certificates", "manage-categories", "manage-certificate-designs" };
        foreach (var p in permissions.Where(p => adminPerms.Contains(p.PermissionName)))
            db.RolePermissions.Add(new RolePermission { RoleId = adminRole.RoleId, PermissionId = p.PermissionId });

        // Media permissions
        var mediaPerms = new[] { "manage-posts", "manage-news" };
        foreach (var p in permissions.Where(p => mediaPerms.Contains(p.PermissionName)))
            db.RolePermissions.Add(new RolePermission { RoleId = mediaRole.RoleId, PermissionId = p.PermissionId });

        // Finance permissions
        var financePerms = new[] { "manage-finance", "view-invoices", "manage-payments", "view-reports", "manage-cashbox" };
        foreach (var p in permissions.Where(p => financePerms.Contains(p.PermissionName)))
            db.RolePermissions.Add(new RolePermission { RoleId = financeRole.RoleId, PermissionId = p.PermissionId });

        db.SaveChanges();

        // Create Super Admin user
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

        // Create Admin user
        var admin = new User
        {
            Username = "manager",
            FullName = "المدير الإداري",
            Email = "manager@training.com",
            Phone = "0501111111",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(bootstrapPassword),
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = DateTime.Now
        };
        db.Users.Add(admin);
        db.SaveChanges();
        db.UserRoles.Add(new UserRole { UserId = admin.UserId, RoleId = adminRole.RoleId });

        // Create Media user
        var media = new User
        {
            Username = "media",
            FullName = "سلمى الإعلامية",
            Email = "media@training.com",
            Phone = "0502222222",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(bootstrapPassword),
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = DateTime.Now
        };
        db.Users.Add(media);
        db.SaveChanges();
        db.UserRoles.Add(new UserRole { UserId = media.UserId, RoleId = mediaRole.RoleId });

        // Create Finance user
        var finance = new User
        {
            Username = "finance",
            FullName = "المحاسب المالي",
            Email = "finance@training.com",
            Phone = "0503333333",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(bootstrapPassword),
            IsActive = true,
            MustChangePassword = true,
            CreatedAt = DateTime.Now
        };
        db.Users.Add(finance);
        db.SaveChanges();
        db.UserRoles.Add(new UserRole { UserId = finance.UserId, RoleId = financeRole.RoleId });

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

    private static void EnsureCashboxPermissions(ECertDbContext db)
    {
        // الصلاحيتان: ترحيل الرصيد (manage-cashbox) وسحب من الصندوق (withdraw-cashbox)
        EnsurePermission(db, "manage-cashbox", "ترحيل أرصدة الدورات إلى الصندوق");
        EnsurePermission(db, "withdraw-cashbox", "سحب من الصندوق العام");

        // مدير المركز (Admin) يرى الصندوق ويحق له ترحيل الأرصدة.
        GrantPermissionIfMissing(db, "Admin", "manage-cashbox");
        // السوبر أدمن لديه الصلاحيتان دائماً (يُمنح في التمهيد الأول).
        GrantPermissionIfMissing(db, "SuperAdmin", "manage-cashbox");
        GrantPermissionIfMissing(db, "SuperAdmin", "withdraw-cashbox");
        // المالي يحرّك الترحيلات لكنه لا يسحب من الصندوق.
        GrantPermissionIfMissing(db, "Finance", "manage-cashbox");

        // إذا كانت قاعدة البيانات قديمة بدون صلاحية "withdraw-cashbox" أصلاً:
        // نضمن وجودها ومنحها للسوبر أدمن فقط حتى لا تسحب الأدوار الأخرى.
    }

    private static void EnsurePermission(ECertDbContext db, string name, string description)
    {
        if (db.Permissions.Any(p => p.PermissionName == name)) return;
        db.Permissions.Add(new Permission { PermissionName = name, Description = description });
        db.SaveChanges();
    }

    private static void GrantPermissionIfMissing(ECertDbContext db, string roleName, string permissionName)
    {
        var role = db.Roles.FirstOrDefault(r => r.RoleName == roleName);
        var perm = db.Permissions.FirstOrDefault(p => p.PermissionName == permissionName);
        if (role == null || perm == null) return;
        if (db.RolePermissions.Any(rp => rp.RoleId == role.RoleId && rp.PermissionId == perm.PermissionId)) return;
        db.RolePermissions.Add(new RolePermission { RoleId = role.RoleId, PermissionId = perm.PermissionId });
        db.SaveChanges();
    }

    private static void EnsureCertificateDesignPermission(ECertDbContext db)
    {
        const string permissionName = "manage-certificate-designs";
        var permission = db.Permissions.FirstOrDefault(p => p.PermissionName == permissionName);
        if (permission == null)
        {
            permission = new Permission
            {
                PermissionName = permissionName,
                Description = "إدارة تصميم الشهادات"
            };
            db.Permissions.Add(permission);
            db.SaveChanges();
        }

        var rolesToGrant = db.Roles
            .Where(role => role.RoleName == "SuperAdmin" || role.RoleName == "Admin")
            .ToList();

        var missingAssignments = rolesToGrant
            .Where(role => !db.RolePermissions.Any(rp => rp.RoleId == role.RoleId && rp.PermissionId == permission.PermissionId))
            .Select(role => new RolePermission { RoleId = role.RoleId, PermissionId = permission.PermissionId })
            .ToList();

        if (missingAssignments.Count > 0)
        {
            db.RolePermissions.AddRange(missingAssignments);
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
}
