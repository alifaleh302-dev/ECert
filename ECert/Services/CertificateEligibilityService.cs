using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

public enum CertificateEligibilityCode
{
    Eligible,
    AlreadyIssued,
    RegistrationNotAccepted,
    CourseNotCompleted,
    CertificateFeeNotSelected,
    InvoiceMissing,
    InvoiceCancelled,
    CertificateFeeUnpaid
}

public sealed record CertificateEligibilityResult(CertificateEligibilityCode Code, string? Message)
{
    public bool Eligible => Code == CertificateEligibilityCode.Eligible;

    public static readonly CertificateEligibilityResult EligibleResult = new(CertificateEligibilityCode.Eligible, null);

    public static CertificateEligibilityResult Blocked(CertificateEligibilityCode code) => code switch
    {
        CertificateEligibilityCode.AlreadyIssued => new(code, "صدرت الشهادة مسبقاً."),
        CertificateEligibilityCode.RegistrationNotAccepted => new(code, "التسجيل غير مقبول."),
        CertificateEligibilityCode.CourseNotCompleted => new(code, "الدورة لم تُكتمل بعد."),
        CertificateEligibilityCode.CertificateFeeNotSelected => new(code, "لم تُفعّل رسوم الشهادة لهذا المتدرب."),
        CertificateEligibilityCode.InvoiceMissing => new(code, "لا توجد فاتورة لهذا التسجيل."),
        CertificateEligibilityCode.InvoiceCancelled => new(code, "فاتورة التسجيل ملغاة."),
        CertificateEligibilityCode.CertificateFeeUnpaid => new(code, "الفاتورة غير مسددة بالكامل."),
        _ => EligibleResult
    };
}

/// <summary>
/// The single gate for certificate issuance. The registration must be accepted, the course
/// completed, and its invoice fully settled. When the course defines a certificate fee, the
/// invoice must also contain the fee line (the applicant opted in or a collector activated it).
/// </summary>
public class CertificateEligibilityService
{
    private readonly ECertDbContext _db;

    public CertificateEligibilityService(ECertDbContext db) => _db = db;

    public async Task<CertificateEligibilityResult> EvaluateAsync(Registration registration)
    {
        if (registration.Course == null)
            registration.Course = await _db.Courses.FirstOrDefaultAsync(c => c.CourseId == registration.CourseId);

        var hasCertificateFee = await _db.CourseFees.AnyAsync(f =>
            f.CourseId == registration.CourseId &&
            f.FeeType == CourseFeeTypes.Certificate &&
            f.IsActive);

        if (registration.Invoice == null)
            registration.Invoice = await _db.Invoices
                .Include(i => i.Items)
                .FirstOrDefaultAsync(i => i.RegistrationId == registration.RegistrationId);

        return Evaluate(registration, hasCertificateFee);
    }

    /// <summary>In-memory evaluation for callers that already loaded Course and Invoice.Items.</summary>
    public CertificateEligibilityResult Evaluate(Registration registration, bool courseHasCertificateFee)
    {
        if (registration.CertificateIssued)
            return CertificateEligibilityResult.Blocked(CertificateEligibilityCode.AlreadyIssued);

        if (!string.Equals(registration.Status, "Accepted", StringComparison.OrdinalIgnoreCase))
            return CertificateEligibilityResult.Blocked(CertificateEligibilityCode.RegistrationNotAccepted);

        if (registration.Course == null || !string.Equals(registration.Course.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            return CertificateEligibilityResult.Blocked(CertificateEligibilityCode.CourseNotCompleted);

        var invoice = registration.Invoice;
        if (invoice == null)
        {
            // A free course without a certificate fee needs no invoice at all.
            if (!courseHasCertificateFee && registration.Course.FinalPrice <= 0)
                return CertificateEligibilityResult.EligibleResult;

            return CertificateEligibilityResult.Blocked(CertificateEligibilityCode.InvoiceMissing);
        }

        if (string.Equals(invoice.Status, "Cancelled", StringComparison.OrdinalIgnoreCase))
            return CertificateEligibilityResult.Blocked(CertificateEligibilityCode.InvoiceCancelled);

        if (courseHasCertificateFee)
        {
            var certificateItem = invoice.Items.FirstOrDefault(i =>
                string.Equals(i.FeeType, CourseFeeTypes.Certificate, StringComparison.OrdinalIgnoreCase));
            if (certificateItem == null)
                return CertificateEligibilityResult.Blocked(CertificateEligibilityCode.CertificateFeeNotSelected);
        }

        if (invoice.TotalAmount > 0 && invoice.PaidAmount < invoice.TotalAmount)
            return CertificateEligibilityResult.Blocked(CertificateEligibilityCode.CertificateFeeUnpaid);

        return CertificateEligibilityResult.EligibleResult;
    }

    /// <summary>The course ids (from the given set) that define an active certificate fee.</summary>
    public async Task<HashSet<int>> CourseIdsWithCertificateFeeAsync(IReadOnlyCollection<int> courseIds)
    {
        if (courseIds.Count == 0) return new HashSet<int>();

        var idsToCheck = courseIds.ToList();
        var ids = await _db.CourseFees
            .Where(f => idsToCheck.Contains(f.CourseId) &&
                        f.FeeType == CourseFeeTypes.Certificate &&
                        f.IsActive)
            .Select(f => f.CourseId)
            .Distinct()
            .ToListAsync();

        return ids.ToHashSet();
    }
}
