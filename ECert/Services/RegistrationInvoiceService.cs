using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

public class RegistrationInvoiceService
{
    private readonly ECertDbContext _db;
    private readonly SystemSettingsService _settings;

    public RegistrationInvoiceService(ECertDbContext db, SystemSettingsService settings)
    {
        _db = db;
        _settings = settings;
    }

    public async Task<Invoice> EnsureForAcceptedAsync(Registration registration, string createdBy)
    {
        if (registration.Invoice != null)
            return registration.Invoice;

        var existing = await _db.Invoices.FirstOrDefaultAsync(i => i.RegistrationId == registration.RegistrationId);
        if (existing != null)
        {
            registration.Invoice = existing;
            return existing;
        }

        if (!string.Equals(registration.Status, "Accepted", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("لا يمكن إنشاء فاتورة قبل قبول التسجيل.");

        if (registration.Course == null)
            registration.Course = await _db.Courses.FirstOrDefaultAsync(c => c.CourseId == registration.CourseId);

        if (registration.Course == null)
            throw new InvalidOperationException("لا يمكن إنشاء الفاتورة لأن الدورة غير موجودة.");

        var originalAmount = registration.Course.FinalPrice;
        var exemptionAmount = Math.Clamp(registration.ExemptionAmount, 0, originalAmount);
        var totalAmount = originalAmount - exemptionAmount;
        var invoice = new Invoice
        {
            InvoiceNumber = await GenerateInvoiceNumberAsync(),
            RegistrationId = registration.RegistrationId,
            Registration = registration,
            TraineeName = registration.FullNameArabic ?? registration.FullName,
            TraineeNameArabic = registration.FullNameArabic ?? registration.FullName,
            TraineeNameEnglish = registration.FullNameEnglish ?? registration.FullName,
            TraineePhone = registration.Phone,
            CourseName = registration.Course.CourseNameArabic ?? registration.Course.CourseName,
            CourseNameEnglish = registration.Course.CourseNameEnglish ?? registration.Course.CourseName,
            CourseNameArabic = registration.Course.CourseNameArabic ?? registration.Course.CourseName,
            OriginalAmount = originalAmount,
            ExemptionAmount = exemptionAmount,
            ExemptionReason = registration.ExemptionReason,
            TotalAmount = totalAmount,
            Status = totalAmount <= 0 ? "Paid" : "Unpaid",
            CreatedAt = DateTime.Now,
            CreatedBy = createdBy
        };

        _db.Invoices.Add(invoice);
        registration.Invoice = invoice;
        return invoice;
    }

    public async Task ApplyExemptionAsync(Registration registration, decimal exemptionAmount, string? reason, string appliedBy)
    {
        if (registration.Course == null)
            registration.Course = await _db.Courses.FirstOrDefaultAsync(c => c.CourseId == registration.CourseId);
        if (registration.Course == null)
            throw new InvalidOperationException("لا يمكن تطبيق الإعفاء لأن الدورة غير موجودة.");

        var originalAmount = registration.Invoice?.OriginalAmount > 0
            ? registration.Invoice.OriginalAmount
            : registration.Course.FinalPrice;
        exemptionAmount = Math.Round(exemptionAmount, 2, MidpointRounding.AwayFromZero);
        if (exemptionAmount < 0 || exemptionAmount > originalAmount)
            throw new InvalidOperationException($"مبلغ الإعفاء يجب أن يكون بين 0 و {originalAmount:N2} {await _settings.GetCurrencySymbolAsync()}.");

        var totalAmount = originalAmount - exemptionAmount;
        if (registration.Invoice != null && registration.Invoice.PaidAmount > totalAmount)
            throw new InvalidOperationException("لا يمكن تطبيق هذا الإعفاء لأن المبلغ المدفوع أكبر من المبلغ المستحق بعد الإعفاء.");

        registration.ExemptionAmount = exemptionAmount;
        registration.ExemptionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        registration.ExemptionAppliedBy = appliedBy;
        registration.ExemptionAppliedAt = DateTime.Now;

        if (registration.Invoice != null)
        {
            registration.Invoice.OriginalAmount = originalAmount;
            registration.Invoice.ExemptionAmount = exemptionAmount;
            registration.Invoice.ExemptionReason = registration.ExemptionReason;
            registration.Invoice.TotalAmount = totalAmount;
            registration.Invoice.Status = registration.Invoice.PaidAmount >= totalAmount
                ? "Paid"
                : registration.Invoice.PaidAmount > 0 ? "PartiallyPaid" : "Unpaid";
        }
    }

    private async Task<string> GenerateInvoiceNumberAsync()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var candidate = $"INV-{DateTime.Now:yyyyMMdd}-{Random.Shared.Next(100000, 999999)}";
            if (!await _db.Invoices.AnyAsync(i => i.InvoiceNumber == candidate))
                return candidate;
        }

        return $"INV-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}"[..40];
    }
}
