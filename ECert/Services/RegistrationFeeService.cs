using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

public sealed record FeeChangeResult(bool Success, string Message);

/// <summary>
/// Owns the money-affecting actions on a registration's optional certificate fee: activating or
/// cancelling it. Every change re-prices the invoice from its lines and refuses to leave the paid
/// amount above the new total.
/// </summary>
public class RegistrationFeeService
{
    private readonly ECertDbContext _db;
    private readonly InvoiceBalanceService _balance;
    private readonly AuditLogService _audit;
    private readonly RegistrationInvoiceService _invoices;

    public RegistrationFeeService(
        ECertDbContext db,
        InvoiceBalanceService balance,
        AuditLogService audit,
        RegistrationInvoiceService invoices)
    {
        _db = db;
        _balance = balance;
        _audit = audit;
        _invoices = invoices;
    }

    public async Task<FeeChangeResult> ActivateCertificateFeeAsync(Registration registration, string actor)
    {
        if (registration.CertificateIssued)
            return new(false, "لا يمكن تعديل رسوم الشهادة بعد إصدارها.");

        if (registration.Status is "Rejected" or "Archived")
            return new(false, "لا يمكن تعديل رسوم الشهادة لتسجيل مرفوض أو مؤرشف.");

        if (registration.Course == null)
        {
            registration.Course = await _db.Courses.Include(c => c.Fees)
                .FirstOrDefaultAsync(c => c.CourseId == registration.CourseId);
        }
        else if (registration.Course.Fees.Count == 0)
        {
            await _db.Entry(registration.Course).Collection(c => c.Fees).LoadAsync();
        }
        if (registration.Course == null)
            return new(false, "الدورة غير موجودة.");

        var courseFee = registration.Course.CertificateFee;
        if (courseFee == null)
            return new(false, "لا توجد رسوم شهادة معرّفة لهذه الدورة. أضفها من شاشة تعديل الدورة أولاً.");

        await using var transaction = await _db.Database.BeginTransactionAsync();

        var snapshot = await UpsertSnapshotAsync(registration, courseFee);
        await _db.SaveChangesAsync();

        if (string.Equals(registration.Status, "Accepted", StringComparison.OrdinalIgnoreCase))
        {
            var invoice = await LoadInvoiceAsync(registration);
            if (invoice == null)
            {
                invoice = await _invoices.EnsureForAcceptedAsync(registration, actor);
            }
            else
            {
                if (invoice.Status == "Cancelled")
                {
                    await transaction.RollbackAsync();
                    return new(false, "الفاتورة ملغاة. استعدها أولاً قبل تفعيل رسوم الشهادة.");
                }

                if (!invoice.Items.Any(i => string.Equals(i.FeeType, CourseFeeTypes.Certificate, StringComparison.OrdinalIgnoreCase)))
                {
                    var sort = invoice.Items.Count == 0 ? 1 : invoice.Items.Max(i => i.SortOrder) + 1;
                    _db.InvoiceItems.Add(new InvoiceItem
                    {
                        InvoiceId = invoice.InvoiceId,
                        Kind = InvoiceItemKinds.Fee,
                        FeeType = CourseFeeTypes.Certificate,
                        Name = courseFee.Name,
                        Description = courseFee.Description,
                        Amount = courseFee.Amount,
                        IsOptional = true,
                        SortOrder = sort,
                        CreatedBy = actor
                    });
                }
            }

            await _db.SaveChangesAsync();
            await _balance.RecalculateAsync(invoice);
        }

        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "ActivateCertificateFee", nameof(Registration), registration.RegistrationId,
            null, $"Amount: {courseFee.Amount:N2}");
        await transaction.CommitAsync();

        return new(true, registration.Status == "Accepted"
            ? "تم تفعيل رسوم الشهادة وإضافتها إلى الفاتورة."
            : "تم تفعيل رسوم الشهادة. ستُضاف إلى الفاتورة عند قبول التسجيل.");
    }

    public async Task<FeeChangeResult> CancelCertificateFeeAsync(Registration registration, string actor)
    {
        if (registration.CertificateIssued)
            return new(false, "لا يمكن إلغاء رسوم الشهادة بعد إصدارها.");

        if (registration.Status is "Rejected" or "Archived")
            return new(false, "لا يمكن تعديل رسوم الشهادة لتسجيل مرفوض أو مؤرشف.");

        await using var transaction = await _db.Database.BeginTransactionAsync();

        var snapshot = await _db.RegistrationFees
            .Where(f => f.RegistrationId == registration.RegistrationId &&
                        f.FeeType == CourseFeeTypes.Certificate)
            .OrderByDescending(f => f.RegistrationFeeId)
            .FirstOrDefaultAsync();
        if (snapshot != null)
            snapshot.IsSelected = false;

        if (string.Equals(registration.Status, "Accepted", StringComparison.OrdinalIgnoreCase))
        {
            var invoice = await LoadInvoiceAsync(registration);
            if (invoice != null)
            {
                if (invoice.Status == "Cancelled")
                {
                    await transaction.RollbackAsync();
                    return new(false, "الفاتورة ملغاة. استعدها أولاً قبل إلغاء رسوم الشهادة.");
                }

                var item = invoice.Items.FirstOrDefault(i =>
                    string.Equals(i.FeeType, CourseFeeTypes.Certificate, StringComparison.OrdinalIgnoreCase));
                if (item != null)
                {
                    var newItemsTotal = invoice.Items
                        .Where(i => i.InvoiceItemId != item.InvoiceItemId)
                        .Sum(i => i.Amount);
                    var newTotal = Math.Max(0m, newItemsTotal - invoice.ExemptionAmount);
                    if (invoice.PaidAmount > newTotal)
                    {
                        await transaction.RollbackAsync();
                        return new(false, "لا يمكن إلغاء رسوم الشهادة لأن المبلغ المدفوع أكبر من المتبقي بعد الإلغاء. استرد أو ألغِ الدفعات أولاً.");
                    }

                    _db.InvoiceItems.Remove(item);
                    await _db.SaveChangesAsync();
                    await _balance.RecalculateAsync(invoice);
                }
            }
        }

        await _db.SaveChangesAsync();
        await _audit.LogAsync(actor, "CancelCertificateFee", nameof(Registration), registration.RegistrationId);
        await transaction.CommitAsync();

        return new(true, "تم إلغاء رسوم الشهادة.");
    }

    private async Task<RegistrationFee> UpsertSnapshotAsync(Registration registration, CourseFee courseFee)
    {
        var snapshot = await _db.RegistrationFees
            .Where(f => f.RegistrationId == registration.RegistrationId &&
                        f.FeeType == CourseFeeTypes.Certificate)
            .OrderByDescending(f => f.RegistrationFeeId)
            .FirstOrDefaultAsync();

        if (snapshot == null)
        {
            snapshot = new RegistrationFee
            {
                RegistrationId = registration.RegistrationId,
                CourseFeeId = courseFee.CourseFeeId,
                Name = courseFee.Name,
                FeeType = courseFee.FeeType,
                Description = courseFee.Description,
                Amount = courseFee.Amount,
                IsOptional = true,
                IsSelected = true
            };
            _db.RegistrationFees.Add(snapshot);
        }
        else
        {
            snapshot.IsSelected = true;
            snapshot.CourseFeeId = courseFee.CourseFeeId;
            snapshot.Name = courseFee.Name;
            snapshot.Description = courseFee.Description;
            snapshot.IsOptional = true;
        }

        return snapshot;
    }

    private async Task<Invoice?> LoadInvoiceAsync(Registration registration)
    {
        var invoice = await _db.Invoices
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.RegistrationId == registration.RegistrationId);
        registration.Invoice = invoice;
        return invoice;
    }
}
