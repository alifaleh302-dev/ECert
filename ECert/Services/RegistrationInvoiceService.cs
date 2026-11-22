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
        {
            registration.Course = await _db.Courses
                .FirstOrDefaultAsync(c => c.CourseId == registration.CourseId);
        }

        if (registration.Course == null)
            throw new InvalidOperationException("لا يمكن إنشاء الفاتورة لأن الدورة غير موجودة.");

        var items = await BuildItemsAsync(registration);
        var originalAmount = items.Sum(i => i.Amount);
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

        foreach (var item in items)
        {
            item.CreatedBy = createdBy;
            invoice.Items.Add(item);
        }

        _db.Invoices.Add(invoice);
        registration.Invoice = invoice;
        return invoice;
    }

    /// <summary>
    /// Prices the invoice from the registration's certificate-fee snapshot. The certificate fee is
    /// charged only when the applicant selected it; a legacy import without a snapshot never
    /// charges it silently.
    /// </summary>
    private async Task<List<InvoiceItem>> BuildItemsAsync(Registration registration)
    {
        var items = new List<InvoiceItem>
        {
            new()
            {
                Kind = InvoiceItemKinds.Course,
                Name = "رسوم الدورة",
                Amount = registration.Course!.FinalPrice,
                IsOptional = false,
                SortOrder = 0
            }
        };

        var certificate = await _db.RegistrationFees
            .Where(f => f.RegistrationId == registration.RegistrationId &&
                        f.IsSelected &&
                        f.FeeType == CourseFeeTypes.Certificate)
            .OrderBy(f => f.RegistrationFeeId)
            .FirstOrDefaultAsync();

        if (certificate != null)
        {
            items.Add(new InvoiceItem
            {
                Kind = InvoiceItemKinds.Fee,
                FeeType = CourseFeeTypes.Certificate,
                Name = certificate.Name,
                Description = certificate.Description,
                Amount = certificate.Amount,
                IsOptional = true,
                SortOrder = 1
            });
        }

        return items;
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
        if (registration.Invoice != null && registration.Invoice.Status == "Cancelled")
            throw new InvalidOperationException("لا يمكن تطبيق الإعفاء على فاتورة ملغاة. استعد الفاتورة أولاً.");
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
