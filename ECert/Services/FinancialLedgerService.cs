using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// The single place that writes to the employee custody ledger and the append-only financial audit
/// trail, and that computes a custody balance. Centralising it means the "collected money lands in
/// the collector's custody" rule and the "every movement is audited" rule are enforced on every
/// path (web, API, refunds, handovers) instead of being re-implemented per controller.
///
/// Methods that <em>add</em> rows do not call <c>SaveChangesAsync</c>: callers run them inside their
/// own transaction (a payment and its custody entry must commit together) and save once.
/// </summary>
public class FinancialLedgerService
{
    private readonly ECertDbContext _db;
    private readonly CertificateSecurityService _security;

    public FinancialLedgerService(ECertDbContext db, CertificateSecurityService security)
    {
        _db = db;
        _security = security;
    }

    /// <summary>Current custody balance of an employee: the signed sum of their custody entries.</summary>
    public async Task<decimal> GetCustodyBalanceAsync(int userId) =>
        await _db.CustodyEntries.Where(e => e.UserId == userId)
            .SumAsync(e => (decimal?)e.Amount) ?? 0m;

    /// <summary>Total custody currently held across every employee (SuperAdmin view).</summary>
    public async Task<decimal> GetTotalCustodyAsync() =>
        await _db.CustodyEntries.SumAsync(e => (decimal?)e.Amount) ?? 0m;

    /// <summary>
    /// The main fund's balance: money that reached it (approved handovers) minus money that left it
    /// (withdrawals and refunds paid directly from the fund). Every component is derived from a
    /// ledger, so the balance is always reproducible. The fund is fed <em>only</em> by custody
    /// handovers — there is no per-course transfer.
    /// </summary>
    public async Task<decimal> GetMainFundBalanceAsync()
    {
        var transferred = await _db.CashboxTransfers.SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var withdrawn = await _db.CashboxWithdrawals.SumAsync(w => (decimal?)w.Amount) ?? 0m;
        var refundedFromFund = await _db.Refunds
            .Where(r => r.PaidFromFund && r.Status == RefundStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        return transferred - withdrawn - refundedFromFund;
    }

    /// <summary>Adds a signed custody movement. Positive credits the employee, negative debits them.</summary>
    public CustodyEntry AddCustodyEntry(
        int userId,
        decimal signedAmount,
        string entryType,
        string actor,
        string? referenceType = null,
        int? referenceId = null,
        string? referenceNumber = null,
        string? note = null)
    {
        var entry = new CustodyEntry
        {
            UserId = userId,
            Amount = signedAmount,
            EntryType = entryType,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            ReferenceNumber = referenceNumber,
            Note = note,
            CreatedBy = actor,
            CreatedAt = DateTime.Now
        };
        _db.CustodyEntries.Add(entry);
        return entry;
    }

    /// <summary>Records a collection into the collector's custody (positive) and audits it.</summary>
    public CustodyEntry AddCollection(int userId, decimal amount, string actor, int paymentId, string? receiptNumber, string? details = null)
    {
        var entry = AddCustodyEntry(userId, Math.Abs(amount), CustodyEntryTypes.Collection, actor,
            referenceType: nameof(Payment), referenceId: paymentId, referenceNumber: receiptNumber, note: details);
        AddAudit(actor, FinancialActionTypes.Collection, Math.Abs(amount),
            referenceType: nameof(Payment), referenceId: paymentId, referenceNumber: receiptNumber, details: details);
        return entry;
    }

    /// <summary>Records a refund paid out of the employee's custody (negative) and audits it.</summary>
    public CustodyEntry AddRefundCustody(int userId, decimal amount, string actor, int refundId, string? receiptNumber, string? details = null)
    {
        var entry = AddCustodyEntry(userId, -Math.Abs(amount), CustodyEntryTypes.Refund, actor,
            referenceType: nameof(Refund), referenceId: refundId, referenceNumber: receiptNumber, note: details);
        AddAudit(actor, FinancialActionTypes.Refund, Math.Abs(amount),
            referenceType: nameof(Refund), referenceId: refundId, referenceNumber: receiptNumber, details: details);
        return entry;
    }

    /// <summary>Records an approved handover out of the employee's custody (negative) and audits it.</summary>
    public CustodyEntry AddHandoverCustody(int userId, decimal amount, string actor, int handoverId, string? details = null)
    {
        var entry = AddCustodyEntry(userId, -Math.Abs(amount), CustodyEntryTypes.Handover, actor,
            referenceType: nameof(CustodyHandover), referenceId: handoverId, note: details);
        AddAudit(actor, FinancialActionTypes.Handover, Math.Abs(amount),
            referenceType: nameof(CustodyHandover), referenceId: handoverId, details: details);
        return entry;
    }

    /// <summary>Appends a row to the append-only financial audit trail. Never updates or deletes.</summary>
    public FinancialAuditEntry AddAudit(
        string userName,
        string actionType,
        decimal amount,
        string? referenceType = null,
        int? referenceId = null,
        string? referenceNumber = null,
        string? details = null)
    {
        var entry = new FinancialAuditEntry
        {
            UserName = string.IsNullOrWhiteSpace(userName) ? "System" : userName,
            ActionType = actionType,
            Amount = amount,
            ReferenceType = referenceType,
            ReferenceId = referenceId,
            ReferenceNumber = referenceNumber,
            Details = details,
            Timestamp = DateTime.Now
        };
        _db.FinancialAuditEntries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Generates and assigns an opaque public id (used by the receipt QR / verification link) to a
    /// payment when it does not already have one. Retries to avoid a collision with an existing id.
    /// </summary>
    public async Task<string> EnsurePaymentPublicIdAsync(Payment payment)
    {
        if (!string.IsNullOrWhiteSpace(payment.PublicId)) return payment.PublicId!;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = _security.GeneratePublicId();
            if (!await _db.Payments.AnyAsync(p => p.PublicId == candidate))
            {
                payment.PublicId = candidate;
                return candidate;
            }
        }

        payment.PublicId = Guid.NewGuid().ToString("N");
        return payment.PublicId;
    }

    /// <summary>Generates and assigns an opaque public id to a refund (see <see cref="EnsurePaymentPublicIdAsync"/>).</summary>
    public async Task<string> EnsureRefundPublicIdAsync(Refund refund)
    {
        if (!string.IsNullOrWhiteSpace(refund.PublicId)) return refund.PublicId!;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var candidate = _security.GeneratePublicId();
            if (!await _db.Refunds.AnyAsync(r => r.PublicId == candidate))
            {
                refund.PublicId = candidate;
                return candidate;
            }
        }

        refund.PublicId = Guid.NewGuid().ToString("N");
        return refund.PublicId;
    }
}
