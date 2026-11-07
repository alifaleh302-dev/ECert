using ECert.Models;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ECert.Data;

public class ECertDbContext : DbContext, IDataProtectionKeyContext
{
    public ECertDbContext(DbContextOptions<ECertDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Instructor> Instructors => Set<Instructor>();
    public DbSet<CourseInstructor> CourseInstructors => Set<CourseInstructor>();
    public DbSet<Registration> Registrations => Set<Registration>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<CertificateDesign> CertificateDesigns => Set<CertificateDesign>();
    public DbSet<CertificateDesignElement> CertificateDesignElements => Set<CertificateDesignElement>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<AppNotification> Notifications => Set<AppNotification>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<PhoneCountry> PhoneCountries => Set<PhoneCountry>();
    public DbSet<SiteSetting> SiteSettings => Set<SiteSetting>();
    public DbSet<HeroSlide> HeroSlides => Set<HeroSlide>();
    public DbSet<HeroAnimatedText> HeroAnimatedTexts => Set<HeroAnimatedText>();
    public DbSet<SocialLink> SocialLinks => Set<SocialLink>();
    public DbSet<ContactInfo> ContactInfos => Set<ContactInfo>();
    public DbSet<StatCard> StatCards => Set<StatCard>();
    public DbSet<HomepageSection> HomepageSections => Set<HomepageSection>();
    public DbSet<ThemeSetting> ThemeSettings => Set<ThemeSetting>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<University> Universities => Set<University>();
    public DbSet<College> Colleges => Set<College>();
    public DbSet<AcademicSpecialization> AcademicSpecializations => Set<AcademicSpecialization>();
    public DbSet<AcademicLevelOption> AcademicLevelOptions => Set<AcademicLevelOption>();
    public DbSet<CashboxTransfer> CashboxTransfers => Set<CashboxTransfer>();
    public DbSet<CashboxWithdrawal> CashboxWithdrawals => Set<CashboxWithdrawal>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Ignore computed properties
        modelBuilder.Entity<Course>().Ignore(c => c.FinalPrice);
        modelBuilder.Entity<Invoice>().Ignore(i => i.RemainingAmount);

        // User Roles
        modelBuilder.Entity<UserRole>()
            .HasOne(ur => ur.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(ur => ur.UserId);

        modelBuilder.Entity<UserRole>()
            .HasOne(ur => ur.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(ur => ur.RoleId);

        // Role Permissions
        modelBuilder.Entity<RolePermission>()
            .HasOne(rp => rp.Role)
            .WithMany(r => r.RolePermissions)
            .HasForeignKey(rp => rp.RoleId);

        modelBuilder.Entity<RolePermission>()
            .HasOne(rp => rp.Permission)
            .WithMany(p => p.RolePermissions)
            .HasForeignKey(rp => rp.PermissionId);

        // Course relationships
        modelBuilder.Entity<Course>()
            .HasOne(c => c.Category)
            .WithMany(cat => cat.Courses)
            .HasForeignKey(c => c.CategoryId);

        modelBuilder.Entity<Course>()
            .HasOne(c => c.Instructor)
            .WithMany(i => i.Courses)
            .HasForeignKey(c => c.InstructorId);

        modelBuilder.Entity<Course>()
            .HasOne(c => c.CertificateDesign)
            .WithMany()
            .HasForeignKey(c => c.CertificateDesignId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<CourseInstructor>()
            .HasOne(ci => ci.Course)
            .WithMany(c => c.CourseInstructors)
            .HasForeignKey(ci => ci.CourseId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<CourseInstructor>()
            .HasOne(ci => ci.Instructor)
            .WithMany(i => i.CourseInstructors)
            .HasForeignKey(ci => ci.InstructorId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CourseInstructor>()
            .HasIndex(ci => new { ci.CourseId, ci.InstructorId })
            .IsUnique();

        // Academic catalog
        modelBuilder.Entity<College>()
            .HasOne(c => c.University)
            .WithMany(u => u.Colleges)
            .HasForeignKey(c => c.UniversityId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AcademicSpecialization>()
            .HasOne(s => s.College)
            .WithMany(c => c.Specializations)
            .HasForeignKey(s => s.CollegeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<AcademicLevelOption>()
            .HasOne(l => l.AcademicSpecialization)
            .WithMany(s => s.Levels)
            .HasForeignKey(l => l.AcademicSpecializationId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<AcademicLevelOption>()
            .HasIndex(l => new { l.AcademicSpecializationId, l.LevelName })
            .IsUnique();

        // Registration
        modelBuilder.Entity<Registration>()
            .HasOne(r => r.Course)
            .WithMany(c => c.Registrations)
            .HasForeignKey(r => r.CourseId);

        modelBuilder.Entity<Registration>()
            .HasOne(r => r.University)
            .WithMany()
            .HasForeignKey(r => r.UniversityId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Registration>()
            .HasOne(r => r.College)
            .WithMany()
            .HasForeignKey(r => r.CollegeId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Registration>()
            .HasOne(r => r.AcademicSpecialization)
            .WithMany()
            .HasForeignKey(r => r.AcademicSpecializationId)
            .OnDelete(DeleteBehavior.Restrict);

        // Invoice
        modelBuilder.Entity<Invoice>()
            .HasOne(i => i.Registration)
            .WithOne(r => r.Invoice)
            .HasForeignKey<Invoice>(i => i.RegistrationId);

        // Payment
        modelBuilder.Entity<Payment>()
            .HasOne(p => p.Invoice)
            .WithMany(i => i.Payments)
            .HasForeignKey(p => p.InvoiceId);

        // Certificate
        modelBuilder.Entity<Certificate>()
            .HasOne(c => c.Registration)
            .WithOne(r => r.Certificate)
            .HasForeignKey<Certificate>(c => c.RegistrationId);

        modelBuilder.Entity<Certificate>()
            .HasIndex(c => c.CertificateNumber)
            .IsUnique();

        modelBuilder.Entity<Certificate>()
            .HasIndex(c => c.PublicId)
            .IsUnique();

        modelBuilder.Entity<Certificate>()
            .HasIndex(c => c.VerificationCode)
            .IsUnique();

        modelBuilder.Entity<Certificate>()
            .HasIndex(c => c.Source);

    }
}
