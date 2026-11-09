using System.Net;
using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Covers the certificate personalisation features: the template background image, the partner
/// organisation logo, and the instructor signature. Rendering is asserted against the real public
/// verification page, and the upload endpoints are driven over HTTP so the whole path is exercised.
/// </summary>
[Collection(AppCollection.Name)]
public class CertificatePersonalisationTests
{
    // A 1x1 PNG; the upload guard checks magic bytes, so the header must be a real PNG signature.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private readonly AppFixture _fixture;

    public CertificatePersonalisationTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Template_background_image_renders_on_the_certificate()
    {
        var certificate = await SeedCertificateAsync(new DesignSetup
        {
            BackgroundImageUrl = "/uploads/designs/backdrop.png"
        });

        var html = await VerifyAsync(certificate.CertificateNumber);

        Assert.Contains("/uploads/designs/backdrop.png", html);
        Assert.Contains("cv-paper__bg", html);
    }

    [Fact]
    public async Task Partner_logo_and_instructor_signature_render_from_the_course()
    {
        var certificate = await SeedCertificateAsync(
            new DesignSetup { PartnerLogoSlots = 1, IncludeSignature = true },
            partners: new[]
            {
                new PartnerSeed("غرفة التجارة", "/uploads/partners/chamber.png", null)
            },
            configureInstructor: instructor => instructor.SignatureImageUrl = "/uploads/signatures/sig.png");

        var html = await VerifyAsync(certificate.CertificateNumber);

        Assert.Contains("/uploads/partners/chamber.png", html);
        Assert.Contains("شعار الجهة الداعمة", html);
        Assert.Contains("/uploads/signatures/sig.png", html);
        Assert.Contains("توقيع المدرب", html);
    }

    [Fact]
    public async Task Several_partners_render_their_own_logo_and_signature_per_slot()
    {
        var certificate = await SeedCertificateAsync(
            new DesignSetup { PartnerLogoSlots = 2, PartnerSignatureSlots = 2 },
            partners: new[]
            {
                new PartnerSeed("الجهة الأولى", "/uploads/partners/first-logo.png", "/uploads/partners/first-sig.png"),
                new PartnerSeed("الجهة الثانية", "/uploads/partners/second-logo.png", "/uploads/partners/second-sig.png")
            });

        var html = await VerifyAsync(certificate.CertificateNumber);

        Assert.Contains("/uploads/partners/first-logo.png", html);
        Assert.Contains("/uploads/partners/first-sig.png", html);
        Assert.Contains("/uploads/partners/second-logo.png", html);
        Assert.Contains("/uploads/partners/second-sig.png", html);
    }

    [Fact]
    public async Task Partner_slots_beyond_the_course_partner_count_render_nothing()
    {
        // Two logo slots are placed but the course only credits one organisation.
        var certificate = await SeedCertificateAsync(
            new DesignSetup { PartnerLogoSlots = 2 },
            partners: new[]
            {
                new PartnerSeed("الجهة الوحيدة", "/uploads/partners/only-logo.png", null)
            });

        var html = await VerifyAsync(certificate.CertificateNumber);

        Assert.Contains("/uploads/partners/only-logo.png", html);
        // Only one logo element produced markup; the empty second slot is skipped entirely.
        Assert.Equal(1, CountOccurrences(html, "شعار الجهة الداعمة"));
    }

    [Fact]
    public async Task Logo_and_signature_layers_are_skipped_when_the_course_has_no_artwork()
    {
        var certificate = await SeedCertificateAsync(
            new DesignSetup { PartnerLogoSlots = 1, IncludeSignature = true });

        var html = await VerifyAsync(certificate.CertificateNumber);

        // The elements stay in the design, but nothing is rendered for them (no broken image).
        Assert.DoesNotContain("شعار الجهة الداعمة", html);
        Assert.DoesNotContain("توقيع المدرب", html);
    }

    [Fact]
    public async Task Uploading_a_signature_stores_it_against_the_instructor()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, name) = await _fixture.Factory.FirstInstructorAsync();

        var response = await admin.PostMultipartAsync(
            $"/Instructors/Edit?id={instructorId}",
            "/Instructors/Edit",
            new[]
            {
                new KeyValuePair<string, string>("InstructorId", instructorId.ToString()),
                new KeyValuePair<string, string>("FullName", name),
                new KeyValuePair<string, string>("IsActive", "true")
            },
            "signature", "signature.png", OnePixelPng, "image/png");

        response.EnsureSuccessStatusCode();

        var url = await _fixture.Factory.WithDbAsync(db => db.Instructors
            .Where(i => i.InstructorId == instructorId)
            .Select(i => i.SignatureImageUrl)
            .FirstAsync());

        Assert.NotNull(url);
        Assert.StartsWith("/uploads/signatures/", url);
    }

    [Fact]
    public async Task Rejecting_a_non_image_signature_upload_keeps_the_existing_one()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, name) = await _fixture.Factory.FirstInstructorAsync();

        var original = await _fixture.Factory.WithDbAsync(async db =>
        {
            var instructor = await db.Instructors.FirstAsync(i => i.InstructorId == instructorId);
            instructor.SignatureImageUrl = "/uploads/signatures/existing.png";
            await db.SaveChangesAsync();
            return instructor.SignatureImageUrl;
        });

        var response = await admin.PostMultipartAsync(
            $"/Instructors/Edit?id={instructorId}",
            "/Instructors/Edit",
            new[]
            {
                new KeyValuePair<string, string>("InstructorId", instructorId.ToString()),
                new KeyValuePair<string, string>("FullName", name),
                new KeyValuePair<string, string>("IsActive", "true")
            },
            "signature", "payload.png", System.Text.Encoding.UTF8.GetBytes("<svg onload=alert(1)>"), "image/png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var url = await _fixture.Factory.WithDbAsync(db => db.Instructors
            .Where(i => i.InstructorId == instructorId)
            .Select(i => i.SignatureImageUrl)
            .FirstAsync());

        Assert.Equal(original, url);
    }

    [Fact]
    public async Task Creating_a_course_with_a_partner_logo_persists_the_upload()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, _) = await _fixture.Factory.FirstInstructorAsync();
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();
        var courseName = "دورة الشراكة " + Guid.NewGuid().ToString("N")[..6];

        var response = await admin.PostMultipartAsync(
            "/CoursesManage/Create",
            "/CoursesManage/Create",
            new[]
            {
                new KeyValuePair<string, string>("CourseNameEnglish", "Partnership course"),
                new KeyValuePair<string, string>("CourseNameArabic", courseName),
                new KeyValuePair<string, string>("CategoryId", categoryId.ToString()),
                new KeyValuePair<string, string>("InstructorId", instructorId.ToString()),
                new KeyValuePair<string, string>("SelectedInstructorIds", instructorId.ToString()),
                new KeyValuePair<string, string>("PartnerRows[0].Name", "غرفة التجارة"),
                new KeyValuePair<string, string>("Status", "Draft")
            },
            "PartnerRows[0].Logo", "logo.png", OnePixelPng, "image/png");

        response.EnsureSuccessStatusCode();

        var partner = await _fixture.Factory.WithDbAsync(async db =>
        {
            var course = await db.Courses.FirstAsync(c => c.CourseNameArabic == courseName);
            return await db.CoursePartners.FirstOrDefaultAsync(p => p.CourseId == course.CourseId);
        });

        Assert.NotNull(partner);
        Assert.Equal("غرفة التجارة", partner!.Name);
        Assert.NotNull(partner.LogoUrl);
        Assert.StartsWith("/uploads/partners/", partner.LogoUrl);
    }

    [Fact]
    public async Task Creating_a_course_with_several_partners_keeps_their_artwork_separate()
    {
        var admin = await SignInAdminAsync();
        var (instructorId, _) = await _fixture.Factory.FirstInstructorAsync();
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();
        var courseName = "دورة الجهات " + Guid.NewGuid().ToString("N")[..6];

        var response = await admin.PostMultipartAsync(
            "/CoursesManage/Create",
            "/CoursesManage/Create",
            new[]
            {
                new KeyValuePair<string, string>("CourseNameEnglish", "Multi-partner course"),
                new KeyValuePair<string, string>("CourseNameArabic", courseName),
                new KeyValuePair<string, string>("CategoryId", categoryId.ToString()),
                new KeyValuePair<string, string>("InstructorId", instructorId.ToString()),
                new KeyValuePair<string, string>("SelectedInstructorIds", instructorId.ToString()),
                new KeyValuePair<string, string>("PartnerRows[0].Name", "الجهة الأولى"),
                new KeyValuePair<string, string>("PartnerRows[1].Name", "الجهة الثانية"),
                new KeyValuePair<string, string>("Status", "Draft")
            },
            new (string, string, byte[], string)[]
            {
                ("PartnerRows[0].Logo", "first.png", OnePixelPng, "image/png"),
                ("PartnerRows[1].Logo", "second.png", OnePixelPng, "image/png")
            });

        response.EnsureSuccessStatusCode();

        var partners = await _fixture.Factory.WithDbAsync(async db =>
        {
            var course = await db.Courses.FirstAsync(c => c.CourseNameArabic == courseName);
            return await db.CoursePartners
                .Where(p => p.CourseId == course.CourseId)
                .OrderBy(p => p.SortOrder)
                .ToListAsync();
        });

        Assert.Equal(2, partners.Count);
        Assert.Equal("الجهة الأولى", partners[0].Name);
        Assert.Equal("الجهة الثانية", partners[1].Name);
        Assert.StartsWith("/uploads/partners/", partners[0].LogoUrl);
        Assert.StartsWith("/uploads/partners/", partners[1].LogoUrl);
        Assert.NotEqual(partners[0].LogoUrl, partners[1].LogoUrl);
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private async Task<string> VerifyAsync(string certificateNumber)
    {
        var anonymous = AppClient.Create(_fixture.Factory);
        var response = await anonymous.PostFormAsync("/Certificates/Verify", "/Certificates/Verify",
            new[] { new KeyValuePair<string, string>("code", certificateNumber) });
        response.EnsureSuccessStatusCode();
        return System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private sealed class DesignSetup
    {
        public string? BackgroundImageUrl { get; init; }
        /// <summary>Number of <c>partner_logo</c> slots to place, targeting partners 0..n-1.</summary>
        public int PartnerLogoSlots { get; init; }
        /// <summary>Number of <c>partner_signature</c> slots to place, targeting partners 0..n-1.</summary>
        public int PartnerSignatureSlots { get; init; }
        public bool IncludeSignature { get; init; }
    }

    private sealed record PartnerSeed(string Name, string? LogoUrl, string? SignatureUrl);

    private sealed record SeededCertificate(string CertificateNumber);

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>
    /// Creates a course, an assigned design, a registration and a certificate, then returns the
    /// certificate number the public verification page can look up.
    /// </summary>
    private async Task<SeededCertificate> SeedCertificateAsync(
        DesignSetup design,
        Action<Course>? configureCourse = null,
        Action<Instructor>? configureInstructor = null,
        IReadOnlyList<PartnerSeed>? partners = null)
    {
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();
        var certificateNumber = "PERS-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        await _fixture.Factory.WithDbAsync(async db =>
        {
            // A dedicated instructor keeps each test isolated from the signature other tests upload.
            var instructor = new Instructor
            {
                FullName = "مدرب التخصيص",
                Specialization = "تخصيص",
                IsActive = true
            };
            configureInstructor?.Invoke(instructor);
            db.Instructors.Add(instructor);
            await db.SaveChangesAsync();

            var course = new Course
            {
                CourseNameEnglish = "Personalisation course",
                CourseNameArabic = "دورة التخصيص",
                CourseName = "دورة التخصيص",
                CategoryId = categoryId,
                InstructorId = instructor.InstructorId,
                Status = "OpenForRegistration"
            };
            configureCourse?.Invoke(course);
            db.Courses.Add(course);
            await db.SaveChangesAsync();

            db.CourseInstructors.Add(new CourseInstructor
            {
                CourseId = course.CourseId,
                InstructorId = instructor.InstructorId,
                SortOrder = 0
            });

            if (partners != null)
            {
                for (var i = 0; i < partners.Count; i++)
                {
                    db.CoursePartners.Add(new CoursePartner
                    {
                        CourseId = course.CourseId,
                        Name = partners[i].Name,
                        LogoUrl = partners[i].LogoUrl,
                        SignatureUrl = partners[i].SignatureUrl,
                        SortOrder = i
                    });
                }
            }

            var visual = CertificateDesignService.CreateDefault("test");
            visual.DesignKey = "personalisation-" + Guid.NewGuid().ToString("N");
            visual.IsPublished = false;
            visual.BackgroundImageUrl = design.BackgroundImageUrl;
            visual.Elements.Clear();
            visual.Elements.Add(Element("field", "trainee_name", 120, 260, 860, 70, 34));
            for (var slot = 0; slot < design.PartnerLogoSlots; slot++)
                visual.Elements.Add(Element("partner_logo", slot.ToString(), 60 + slot * 200, 60, 170, 170));
            for (var slot = 0; slot < design.PartnerSignatureSlots; slot++)
                visual.Elements.Add(Element("partner_signature", slot.ToString(), 60 + slot * 260, 660, 240, 90));
            if (design.IncludeSignature)
                visual.Elements.Add(Element("instructor_signature", "", 800, 620, 240, 90));
            db.CertificateDesigns.Add(visual);
            await db.SaveChangesAsync();

            course.CertificateDesignId = visual.CertificateDesignId;

            var registration = new Registration
            {
                RequestNumber = "REG-" + Guid.NewGuid().ToString("N")[..10],
                FullName = "متدرب التخصيص",
                FullNameArabic = "متدرب التخصيص",
                FullNameEnglish = "Personalisation Trainee",
                Phone = "+967700000001",
                CourseId = course.CourseId,
                Status = "Accepted",
                RegistrationDate = DateTime.Now
            };
            db.Registrations.Add(registration);
            await db.SaveChangesAsync();

            db.Certificates.Add(new Certificate
            {
                CertificateNumber = certificateNumber,
                PublicId = Guid.NewGuid().ToString("N")[..20],
                VerificationCode = Guid.NewGuid().ToString("N")[..12],
                RegistrationId = registration.RegistrationId,
                TraineeName = registration.FullName,
                TraineeNameArabic = registration.FullNameArabic,
                TraineeNameEnglish = registration.FullNameEnglish,
                CourseName = course.CourseNameArabic,
                CourseNameArabic = course.CourseNameArabic,
                CourseNameEnglish = course.CourseNameEnglish,
                IssuedBy = "admin",
                Status = "Valid",
                Source = "New",
                IssueDate = DateTime.Now
            });

            await db.SaveChangesAsync();
            return true;
        });

        return new SeededCertificate(certificateNumber);
    }

    private static CertificateDesignElement Element(
        string type, string fieldKey, int x, int y, int width, int height, int fontSize = 24)
        => new()
        {
            ElementType = type,
            FieldKey = fieldKey,
            X = x,
            Y = y,
            Width = width,
            Height = height,
            FontSize = fontSize,
            FontFamily = "Tajawal",
            FontColor = "#172033",
            FontWeight = "600",
            TextAlign = "center",
            IsVisible = true,
            SortOrder = 1,
            ZIndex = 1
        };
}
