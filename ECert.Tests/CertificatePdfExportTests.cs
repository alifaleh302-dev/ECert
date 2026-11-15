using System.Net;
using System.Text.RegularExpressions;
using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Covers the certificate PDF export: the merged print-ready file, the per-deployment batch ceiling
/// the SuperAdmin controls, and the public-download switch. Rendering is exercised over real HTTP so
/// the whole path — permissions, settings lookup, SkiaSharp rendering — runs for real.
/// </summary>
[Collection(AppCollection.Name)]
public class CertificatePdfExportTests
{
    private readonly AppFixture _fixture;

    public CertificatePdfExportTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Admin_can_export_selected_certificates_as_one_merged_pdf()
    {
        var first = await SeedCertificateAsync();
        var second = await SeedCertificateAsync();
        var admin = await SignInAdminAsync();

        var response = await admin.PostFormAsync("/Certificates", "/Certificates/ExportSelectedPdf", new[]
        {
            new KeyValuePair<string, string>("certificateIds", first.CertificateId.ToString()),
            new KeyValuePair<string, string>("certificateIds", second.CertificateId.ToString())
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));

        // One certificate per page: two certificates must produce two pages.
        var content = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Equal(2, Regex.Matches(content, "/MediaBox").Count);

        // The default page size is A4 landscape, so the first MediaBox must be wider than tall.
        var box = Regex.Match(content, @"/MediaBox\s*\[\s*0\s+0\s+([\d.]+)\s+([\d.]+)");
        Assert.True(box.Success, "the PDF must declare a MediaBox");
        Assert.True(double.Parse(box.Groups[1].Value) > double.Parse(box.Groups[2].Value),
            "A4 landscape pages are wider than they are tall");
    }

    [Fact]
    public async Task Export_draws_the_template_background_image()
    {
        // Regression: the exported PDF only painted the background colour and border; the uploaded
        // template artwork shown on screen was never drawn, so the printed certificate looked bare.
        // The page must now embed the background image (the PDF then carries an XObject image).
        var imagePath = "/uploads/designs/pdf-bg-" + Guid.NewGuid().ToString("N") + ".png";
        var webRoot = _fixture.Factory.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath;
        var absolute = Path.Combine(webRoot, imagePath.TrimStart('/'));
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        // A 2x2 PNG so the decoder produces a real bitmap rather than a degenerate 1x1.
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEElEQVR4nGP4z8AARAwQCgAf7gP9i18U1AAAAABJRU5ErkJggg==");
        await File.WriteAllBytesAsync(absolute, png);

        try
        {
            var seeded = await SeedCertificateAsync(imagePath);
            var admin = await SignInAdminAsync();

            var response = await admin.PostFormAsync("/Certificates", "/Certificates/ExportSelectedPdf", new[]
            {
                new KeyValuePair<string, string>("certificateIds", seeded.CertificateId.ToString())
            });
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var content = System.Text.Encoding.Latin1.GetString(bytes);
            Assert.Contains("/Subtype /Image", content);
        }
        finally
        {
            if (File.Exists(absolute)) File.Delete(absolute);
        }
    }

    [Fact]
    public async Task Export_is_refused_when_the_selection_exceeds_the_configured_limit()
    {
        await SetBatchLimitAsync(1);
        var first = await SeedCertificateAsync();
        var second = await SeedCertificateAsync();
        var admin = await SignInAdminAsync();

        var response = await admin.PostFormAsync("/Certificates", "/Certificates/ExportSelectedPdf", new[]
        {
            new KeyValuePair<string, string>("certificateIds", first.CertificateId.ToString()),
            new KeyValuePair<string, string>("certificateIds", second.CertificateId.ToString())
        });

        // Over the ceiling the action redirects back to the list instead of rendering a PDF, so the
        // client (which follows redirects) lands on the certificate list, not on a PDF.
        Assert.NotEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        var finalPath = response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
        Assert.Equal("/Certificates", finalPath);
    }

    [Fact]
    public async Task Public_pdf_download_is_hidden_until_the_superadmin_enables_it()
    {
        var seeded = await SeedCertificateAsync();
        var security = _fixture.Factory.Services.GetRequiredService<CertificateSecurityService>();
        var signature = security.ComputeSignature(seeded.PublicId);
        var link = $"/v/{seeded.PublicId}?sig={Uri.EscapeDataString(signature)}";

        var anonymous = AppClient.Create(_fixture.Factory);
        var hidden = await anonymous.GetStringAsync(link);
        Assert.DoesNotContain("/Certificates/PublicPdf", hidden);

        // The direct endpoint is closed too while the switch is off.
        var closed = await anonymous.GetAsync($"/Certificates/PublicPdf?publicId={seeded.PublicId}&sig={Uri.EscapeDataString(signature)}");
        Assert.Equal(HttpStatusCode.NotFound, closed.StatusCode);

        await SetPublicDownloadAsync(true);

        var shown = await anonymous.GetStringAsync(link);
        Assert.Contains("/Certificates/PublicPdf", shown);

        var download = await anonymous.GetAsync($"/Certificates/PublicPdf?publicId={seeded.PublicId}&sig={Uri.EscapeDataString(signature)}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Public_pdf_requires_a_valid_signature()
    {
        var seeded = await SeedCertificateAsync();
        await SetPublicDownloadAsync(true);

        var anonymous = AppClient.Create(_fixture.Factory);
        var forged = await anonymous.GetAsync($"/Certificates/PublicPdf?publicId={seeded.PublicId}&sig=not-a-real-signature");
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
    }

    [Fact]
    public async Task Export_settings_section_is_reachable_by_the_admin()
    {
        var admin = await SignInAdminAsync();
        Assert.True(await admin.CanOpenAsync("/CertificateExportSettings"));

        var html = System.Net.WebUtility.HtmlDecode(await admin.GetStringAsync("/CertificateExportSettings"));
        Assert.Contains("تصدير الشهادات", html);
    }

    private async Task SetBatchLimitAsync(int limit)
        => await _fixture.Factory.WithDbAsync(async db =>
        {
            var settings = await db.CertificateExportSettings.FirstAsync();
            settings.MaxCertificatesPerExport = limit;
            await db.SaveChangesAsync();
            return true;
        });

    private async Task SetPublicDownloadAsync(bool allowed)
        => await _fixture.Factory.WithDbAsync(async db =>
        {
            var settings = await db.CertificateExportSettings.FirstAsync();
            settings.AllowPublicDownload = allowed;
            await db.SaveChangesAsync();
            return true;
        });

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private sealed record SeededCertificate(int CertificateId, string PublicId);

    /// <summary>
    /// Creates a course with an assigned design (trainee name, QR and barcode) plus a valid
    /// certificate, so the export renders real elements rather than an empty page.
    /// </summary>
    private async Task<SeededCertificate> SeedCertificateAsync(string? backgroundImageUrl = null)
    {
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();
        var certificateNumber = "PDF-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var publicId = "pdf" + Guid.NewGuid().ToString("N")[..14];

        return await _fixture.Factory.WithDbAsync(async db =>
        {
            var instructor = new Instructor { FullName = "مدرب التصدير", IsActive = true };
            db.Instructors.Add(instructor);
            await db.SaveChangesAsync();

            var course = new Course
            {
                CourseNameEnglish = "Export course",
                CourseNameArabic = "دورة التصدير",
                CourseName = "دورة التصدير",
                CategoryId = categoryId,
                InstructorId = instructor.InstructorId,
                Status = "OpenForRegistration"
            };
            db.Courses.Add(course);
            await db.SaveChangesAsync();

            var design = CertificateDesignService.CreateDefault("export");
            design.DesignKey = "export-" + Guid.NewGuid().ToString("N");
            design.IsPublished = false;
            design.BackgroundImageUrl = backgroundImageUrl;
            design.Elements.Clear();
            design.Elements.Add(Element("field", "trainee_name", 120, 260, 860, 70, 34));
            design.Elements.Add(Element("qr", "", 860, 560, 160, 160));
            design.Elements.Add(Element("barcode", "certificate_number", 120, 640, 520, 80));
            db.CertificateDesigns.Add(design);
            await db.SaveChangesAsync();

            course.CertificateDesignId = design.CertificateDesignId;

            var registration = new Registration
            {
                RequestNumber = "REG-" + Guid.NewGuid().ToString("N")[..10],
                FullName = "متدرب التصدير",
                FullNameArabic = "متدرب التصدير",
                FullNameEnglish = "Export Trainee",
                Phone = "+967700000002",
                CourseId = course.CourseId,
                Status = "Accepted",
                RegistrationDate = DateTime.Now
            };
            db.Registrations.Add(registration);
            await db.SaveChangesAsync();

            var certificate = new Certificate
            {
                CertificateNumber = certificateNumber,
                PublicId = publicId,
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
            };
            db.Certificates.Add(certificate);
            await db.SaveChangesAsync();

            return new SeededCertificate(certificate.CertificateId, publicId);
        });
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
