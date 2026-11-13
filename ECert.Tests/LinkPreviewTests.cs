using System.Net;
using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Covers the "smart link card" feature end to end: the public share endpoints return real PNGs,
/// and the pages a trainee shares advertise the right preview image (a certificate card for a
/// certificate link, the centre logo card everywhere else).
/// </summary>
[Collection(AppCollection.Name)]
public class LinkPreviewTests
{
    private readonly AppFixture _fixture;

    public LinkPreviewTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Site_share_image_is_a_png()
    {
        var client = AppClient.Create(_fixture.Factory);
        var response = await client.GetAsync("/share/site.png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes.Take(4).ToArray());
    }

    [Fact]
    public async Task Homepage_advertises_the_logo_card_as_its_preview()
    {
        var client = AppClient.Create(_fixture.Factory);
        var html = await client.GetStringAsync("/");

        Assert.Contains("/share/site.png", html);
        Assert.Contains("og:image", html);
        Assert.Contains("summary_large_image", html);
    }

    [Fact]
    public async Task Certificate_share_endpoint_returns_a_png_for_a_signed_link_and_a_card_for_a_forged_one()
    {
        var (_, publicId, signature) = await SeedCertificateAsync();
        var client = AppClient.Create(_fixture.Factory);

        var good = await client.GetAsync($"/share/certificate/{publicId}.png?sig={Uri.EscapeDataString(signature)}");
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("image/png", good.Content.Headers.ContentType?.MediaType);
        var goodBytes = await good.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, goodBytes.Take(4).ToArray());

        // A forged signature still yields a card (the neutral logo card), never a 500 or a leak.
        var forged = await client.GetAsync($"/share/certificate/{publicId}.png?sig=not-a-real-signature");
        Assert.Equal(HttpStatusCode.OK, forged.StatusCode);
        var forgedBytes = await forged.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, forgedBytes.Take(4).ToArray());
    }

    [Fact]
    public async Task Certificate_page_points_its_preview_at_the_certificate_card()
    {
        var (certificateNumber, publicId, signature) = await SeedCertificateAsync();
        var client = AppClient.Create(_fixture.Factory);

        var html = System.Net.WebUtility.HtmlDecode(await client.GetStringAsync(
            $"/v/{publicId}?sig={Uri.EscapeDataString(signature)}"));

        Assert.Contains("/share/certificate/", html);
        Assert.Contains(publicId, html);
        Assert.Contains("og:image", html);
        Assert.Contains(certificateNumber, html); // the description names the certificate number
    }

    /// <summary>
    /// Seeds a course + registration + valid certificate, then returns its number, public id and
    /// the signature the public link requires.
    /// </summary>
    private async Task<(string Number, string PublicId, string Signature)> SeedCertificateAsync()
    {
        var (categoryId, _) = await _fixture.Factory.FirstCategoryAsync();
        var certificateNumber = "SHARE-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var publicId = "share" + Guid.NewGuid().ToString("N")[..14];

        await _fixture.Factory.WithDbAsync(async db =>
        {
            var instructor = new Instructor { FullName = "مدرب المشاركة", IsActive = true };
            db.Instructors.Add(instructor);
            await db.SaveChangesAsync();

            var course = new Course
            {
                CourseNameEnglish = "Share course",
                CourseNameArabic = "دورة المشاركة",
                CourseName = "دورة المشاركة",
                CategoryId = categoryId,
                InstructorId = instructor.InstructorId,
                Status = "OpenForRegistration"
            };
            db.Courses.Add(course);
            await db.SaveChangesAsync();

            var registration = new Registration
            {
                RequestNumber = "REG-" + Guid.NewGuid().ToString("N")[..10],
                FullName = "متدرب المشاركة",
                FullNameArabic = "متدرب المشاركة",
                FullNameEnglish = "Share Trainee",
                Phone = "+967700000009",
                CourseId = course.CourseId,
                Status = "Accepted",
                RegistrationDate = DateTime.Now
            };
            db.Registrations.Add(registration);
            await db.SaveChangesAsync();

            db.Certificates.Add(new Certificate
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
            });
            await db.SaveChangesAsync();
            return true;
        });

        var security = _fixture.Factory.Services.GetRequiredService<CertificateSecurityService>();
        return (certificateNumber, publicId, security.ComputeSignature(publicId));
    }
}
