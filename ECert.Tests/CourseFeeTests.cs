using ECert.Data;
using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Covers the optional certificate fee at the service layer: a free course can still carry the
/// fee, a paid course charges its price plus the fee, the collector can turn the fee on and off,
/// and a certificate is only issuable once the whole invoice is paid.
/// </summary>
[Collection(AppCollection.Name)]
public class CourseFeeTests
{
    private readonly AppFixture _fixture;

    public CourseFeeTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_free_course_with_a_selected_certificate_fee_charges_only_that_fee()
    {
        var courseId = await CreateCourseAsync(0m, 100m);
        var registrationId = await CreateAcceptedRegistrationAsync(courseId, wantsCertificate: true);

        var invoice = await EnsureInvoiceAsync(registrationId);

        Assert.Equal(100m, invoice.TotalAmount);
        Assert.Equal("Unpaid", invoice.Status);
        var items = await ItemsAsync(invoice.InvoiceId);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Kind == InvoiceItemKinds.Course && i.Amount == 0m);
        Assert.Contains(items, i => i.FeeType == CourseFeeTypes.Certificate && i.Amount == 100m && i.IsOptional);
    }

    [Fact]
    public async Task A_paid_course_with_a_selected_certificate_fee_charges_price_plus_fee()
    {
        var courseId = await CreateCourseAsync(500m, 100m);
        var registrationId = await CreateAcceptedRegistrationAsync(courseId, wantsCertificate: true);

        var invoice = await EnsureInvoiceAsync(registrationId);

        Assert.Equal(600m, invoice.TotalAmount);
        Assert.Equal("Unpaid", invoice.Status);
        var items = await ItemsAsync(invoice.InvoiceId);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.Kind == InvoiceItemKinds.Course && i.Amount == 500m);
        Assert.Contains(items, i => i.FeeType == CourseFeeTypes.Certificate && i.Amount == 100m);
    }

    [Fact]
    public async Task Declining_the_certificate_fee_on_a_free_course_leaves_a_zero_invoice()
    {
        var courseId = await CreateCourseAsync(0m, 100m);
        var registrationId = await CreateAcceptedRegistrationAsync(courseId);

        var invoice = await EnsureInvoiceAsync(registrationId);

        Assert.Equal(0m, invoice.TotalAmount);
        Assert.Equal("Paid", invoice.Status);
        Assert.DoesNotContain(await ItemsAsync(invoice.InvoiceId), i => i.FeeType == CourseFeeTypes.Certificate);
    }

    [Fact]
    public async Task Declining_the_certificate_fee_on_a_paid_course_charges_only_the_price()
    {
        var courseId = await CreateCourseAsync(500m, 100m);
        var registrationId = await CreateAcceptedRegistrationAsync(courseId);

        var invoice = await EnsureInvoiceAsync(registrationId);

        Assert.Equal(500m, invoice.TotalAmount);
        Assert.Equal("Unpaid", invoice.Status);
        Assert.DoesNotContain(await ItemsAsync(invoice.InvoiceId), i => i.FeeType == CourseFeeTypes.Certificate);
    }

    [Fact]
    public async Task Activating_the_certificate_fee_adds_a_line_and_cancelling_removes_it()
    {
        var courseId = await CreateCourseAsync(0m, 100m);
        var registrationId = await CreateAcceptedRegistrationAsync(courseId);
        var invoice = await EnsureInvoiceAsync(registrationId);
        Assert.Equal(0m, invoice.TotalAmount);
        Assert.Equal("Paid", invoice.Status);

        var result = await ActivateCertificateAsync(registrationId);
        Assert.True(result.Success, result.Message);

        invoice = await ReloadInvoiceAsync(registrationId);
        Assert.Equal(100m, invoice.TotalAmount);
        Assert.Equal("Unpaid", invoice.Status);
        Assert.Single(await ItemsAsync(invoice.InvoiceId), i => i.FeeType == CourseFeeTypes.Certificate);

        var cancel = await CancelCertificateAsync(registrationId);
        Assert.True(cancel.Success, cancel.Message);

        invoice = await ReloadInvoiceAsync(registrationId);
        Assert.Equal(0m, invoice.TotalAmount);
        Assert.Equal("Paid", invoice.Status);
        Assert.DoesNotContain(await ItemsAsync(invoice.InvoiceId), i => i.FeeType == CourseFeeTypes.Certificate);
    }

    [Fact]
    public async Task Cancelling_a_paid_certificate_fee_is_refused()
    {
        var courseId = await CreateCourseAsync(0m, 100m);
        var registrationId = await CreateAcceptedRegistrationAsync(courseId);
        var invoice = await EnsureInvoiceAsync(registrationId);

        var activate = await ActivateCertificateAsync(registrationId);
        Assert.True(activate.Success, activate.Message);
        await _fixture.Factory.AddPaymentAsync(invoice.InvoiceId, 100m);

        var cancel = await CancelCertificateAsync(registrationId);
        Assert.False(cancel.Success);
        Assert.Contains("المبلغ المدفوع", cancel.Message);
    }

    [Fact]
    public async Task Certificate_issuance_is_blocked_until_the_full_invoice_is_paid()
    {
        var courseId = await CreateCourseAsync(500m, 100m);
        await SetCourseStatusAsync(courseId, "Completed");
        var registrationId = await CreateAcceptedRegistrationAsync(courseId, wantsCertificate: true);
        var invoice = await EnsureInvoiceAsync(registrationId);

        await _fixture.Factory.AddPaymentAsync(invoice.InvoiceId, 500m);
        var partiallyPaid = await EvaluateAsync(registrationId);
        Assert.Equal(CertificateEligibilityCode.CertificateFeeUnpaid, partiallyPaid.Code);

        await _fixture.Factory.AddPaymentAsync(invoice.InvoiceId, 100m);
        var paid = await EvaluateAsync(registrationId);
        Assert.True(paid.Eligible, paid.Message);
    }

    [Fact]
    public async Task Certificate_issuance_is_blocked_when_the_trainee_declined_the_fee()
    {
        var courseId = await CreateCourseAsync(0m, 100m);
        await SetCourseStatusAsync(courseId, "Completed");
        var registrationId = await CreateAcceptedRegistrationAsync(courseId);
        await EnsureInvoiceAsync(registrationId);

        var result = await EvaluateAsync(registrationId);

        Assert.Equal(CertificateEligibilityCode.CertificateFeeNotSelected, result.Code);
    }

    [Fact]
    public async Task A_free_course_without_a_certificate_fee_keeps_free_certificate_issuance()
    {
        var courseId = await CreateCourseAsync(0m);
        await SetCourseStatusAsync(courseId, "Completed");
        var registrationId = await CreateAcceptedRegistrationAsync(courseId);
        await EnsureInvoiceAsync(registrationId);

        var result = await EvaluateAsync(registrationId);

        Assert.True(result.Eligible, result.Message);
    }

    [Fact]
    public async Task A_paid_course_without_a_certificate_fee_still_requires_payment()
    {
        var courseId = await CreateCourseAsync(500m);
        await SetCourseStatusAsync(courseId, "Completed");
        var registrationId = await CreateAcceptedRegistrationAsync(courseId);
        var invoice = await EnsureInvoiceAsync(registrationId);

        var blocked = await EvaluateAsync(registrationId);
        Assert.Equal(CertificateEligibilityCode.CertificateFeeUnpaid, blocked.Code);

        await _fixture.Factory.AddPaymentAsync(invoice.InvoiceId, 500m);
        var paid = await EvaluateAsync(registrationId);
        Assert.True(paid.Eligible, paid.Message);
    }

    private async Task<int> CreateCourseAsync(decimal price, decimal certificateFee = 0m)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var course = new Course
        {
            CourseName = $"دورة رسوم {suffix}",
            CourseNameArabic = $"دورة رسوم {suffix}",
            CourseNameEnglish = $"Fees course {suffix}",
            CategoryId = await db.Categories.Select(c => c.CategoryId).FirstAsync(),
            InstructorId = await db.Instructors.Select(i => i.InstructorId).FirstAsync(),
            Price = price,
            Status = "OpenForRegistration",
            CreatedAt = DateTime.Now
        };
        db.Courses.Add(course);
        await db.SaveChangesAsync();

        if (certificateFee > 0)
        {
            db.CourseFees.Add(new CourseFee
            {
                CourseId = course.CourseId,
                Name = "رسوم الشهادة",
                FeeType = CourseFeeTypes.Certificate,
                Amount = certificateFee,
                IsOptional = true,
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        return course.CourseId;
    }

    private async Task<int> CreateAcceptedRegistrationAsync(int courseId, bool wantsCertificate = false)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var registration = new Registration
        {
            RequestNumber = $"REG-FEE-{Guid.NewGuid():N}"[..24],
            FullName = "متدرب رسوم",
            FullNameArabic = "متدرب رسوم",
            FullNameEnglish = "Fee Trainee",
            Gender = "Male",
            Phone = $"+9677{Random.Shared.Next(10000000, 99999999)}",
            CourseId = courseId,
            Status = "Accepted",
            AcceptedDate = DateTime.Now,
            RegistrationDate = DateTime.Now
        };

        if (wantsCertificate)
        {
            var fee = await db.CourseFees.FirstAsync(f =>
                f.CourseId == courseId && f.FeeType == CourseFeeTypes.Certificate);
            registration.Fees.Add(new RegistrationFee
            {
                CourseFeeId = fee.CourseFeeId,
                Name = fee.Name,
                FeeType = fee.FeeType,
                Description = fee.Description,
                Amount = fee.Amount,
                IsOptional = true,
                IsSelected = true
            });
        }

        db.Registrations.Add(registration);
        await db.SaveChangesAsync();
        return registration.RegistrationId;
    }

    private async Task<Invoice> EnsureInvoiceAsync(int registrationId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var invoiceService = scope.ServiceProvider.GetRequiredService<RegistrationInvoiceService>();
        var registration = await db.Registrations
            .Include(r => r.Course)
            .Include(r => r.Invoice)
            .FirstAsync(r => r.RegistrationId == registrationId);

        var invoice = await invoiceService.EnsureForAcceptedAsync(registration, "test");
        await db.SaveChangesAsync();
        return invoice;
    }

    private async Task<Invoice> ReloadInvoiceAsync(int registrationId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        return await db.Invoices
            .Include(i => i.Items)
            .FirstAsync(i => i.RegistrationId == registrationId);
    }

    private async Task<List<InvoiceItem>> ItemsAsync(int invoiceId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        return await db.InvoiceItems.Where(i => i.InvoiceId == invoiceId).ToListAsync();
    }

    private async Task<FeeChangeResult> ActivateCertificateAsync(int registrationId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var feeService = scope.ServiceProvider.GetRequiredService<RegistrationFeeService>();
        var registration = await db.Registrations.Include(r => r.Course).FirstAsync(r => r.RegistrationId == registrationId);
        return await feeService.ActivateCertificateFeeAsync(registration, "test");
    }

    private async Task<FeeChangeResult> CancelCertificateAsync(int registrationId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var feeService = scope.ServiceProvider.GetRequiredService<RegistrationFeeService>();
        var registration = await db.Registrations.Include(r => r.Course).FirstAsync(r => r.RegistrationId == registrationId);
        return await feeService.CancelCertificateFeeAsync(registration, "test");
    }

    private async Task<CertificateEligibilityResult> EvaluateAsync(int registrationId)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var eligibility = scope.ServiceProvider.GetRequiredService<CertificateEligibilityService>();
        var registration = await db.Registrations
            .Include(r => r.Course)
            .Include(r => r.Invoice).ThenInclude(i => i!.Items)
            .FirstAsync(r => r.RegistrationId == registrationId);
        return await eligibility.EvaluateAsync(registration);
    }

    private async Task SetCourseStatusAsync(int courseId, string status)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
        var course = await db.Courses.FirstAsync(c => c.CourseId == courseId);
        course.Status = status;
        await db.SaveChangesAsync();
    }
}
