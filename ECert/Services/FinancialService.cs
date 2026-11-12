using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>Result of a refund attempt, so controllers can surface a clear Arabic message.</summary>
public sealed record RefundResult(bool Success, string Message, Refund? Refund = null);

/// <summary>
/// Owns the invoice-balance invariant and the refund workflow. The paid amount of an invoice is
/// always <c>SUM(active payments) − SUM(refunds)</c>, derived from the ledgers rather than
/// incremented in place, so it can never drift after a refund, cancellation or failed write. The
/// refund itself is a new dated movement (never a deletion of an old payment) that also debits the
/// custody of the employee who performed it — all inside one transaction.
/// </summary>
public class FinancialService
{
    private readonly ECertDbContext _db;
    private readonly FinancialLedgerService _ledger;

    public FinancialService(ECertDbContext db, FinancialLedgerService ledger)
    {
        _db = db;
        _ledger = ledger;
    }

    /// <summary>
    /// Recomputes <c>PaidAmount</c> and the status from the invoice's active payments and refunds.
    /// A cancelled invoice keeps its cancelled status.
    /// </summary>
    public async Task RecomputeInvoiceAsync(Invoice invoice)
    {
        var paid = await _db.Payments
            .Where(p => p.InvoiceId == invoice.InvoiceId && !p.IsCancelled)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var refunded = await _db.Refunds
            .Where(r => r.InvoiceId == invoice.InvoiceId)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        var net = paid - refunded;

        invoice.PaidAmount = net;
        invoice.Status = invoice.Status == "Cancelled"
            ? "Cancelled"
            : net >= invoice.TotalAmount && invoice.TotalAmount > 0 ? "Paid"
            : net > 0 ? "PartiallyPaid"
            : "Unpaid";
    }

    /// <summary>Total refunded on an invoice.</summary>
    public async Task<decimal> GetRefundedAmountAsync(int invoiceId) =>
        await _db.Refunds.Where(r => r.InvoiceId == invoiceId)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;

    /// <summary>
    /// Creates a refund against an invoice. The amount may not exceed the invoice's currently paid
    /// amount, a reason is mandatory, and the refund debits the performing employee's custody. The
    /// caller must already be inside a transaction; this method saves but does not commit.
    /// </summary>
    public async Task<RefundResult> CreateRefundAsync(
        Invoice invoice,
        decimal amount,
        string reason,
        string refundMethod,
        string? receiptNumber,
        int performingUserId,
        string performingUserName)
    {
        if (invoice.Status == "Cancelled")
            return new RefundResult(false, "لا يمكن استرداد رسوم من فاتورة ملغية.");

        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0)
            return new RefundResult(false, "مبلغ الاسترداد يجب أن يكون أكبر من صفر.");

        if (string.IsNullOrWhiteSpace(reason))
            return new RefundResult(false, "سبب الاسترداد مطلوب.");

        // The refundable ceiling is the invoice's net paid amount (payments minus prior refunds).
        await RecomputeInvoiceAsync(invoice);
        if (amount > invoice.PaidAmount)
            return new RefundResult(false, $"مبلغ الاسترداد ({amount:N2}) أكبر من المبلغ المدفوع ({invoice.PaidAmount:N2}).");

        var refund = new Refund
        {
            InvoiceId = invoice.InvoiceId,
            RegistrationId = invoice.RegistrationId,
            Amount = amount,
            Reason = reason.Trim(),
            RefundMethod = string.IsNullOrWhiteSpace(refundMethod) ? "Cash" : refundMethod,
            ReceiptNumber = string.IsNullOrWhiteSpace(receiptNumber) ? null : receiptNumber.Trim(),
            RefundDate = DateTime.Now,
            RefundedBy = performingUserName,
            CreatedAt = DateTime.Now
        };
        await _ledger.EnsureRefundPublicIdAsync(refund);
        _db.Refunds.Add(refund);

        // Save once so the refund has its key; the custody entry and audit row then reference it.
        await _db.SaveChangesAsync();

        // Money leaves the custody of the employee who paid it out; the movement is audited too.
        _ledger.AddRefundCustody(performingUserId, amount, performingUserName, refund.RefundId,
            refund.ReceiptNumber, $"استرداد على الفاتورة {invoice.InvoiceNumber}: {refund.Reason}");

        await RecomputeInvoiceAsync(invoice);
        await _db.SaveChangesAsync();

        return new RefundResult(true, $"تم تسجيل استرداد بقيمة {amount:N2} ريال بنجاح.", refund);
    }
}
