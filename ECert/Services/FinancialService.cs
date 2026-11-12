using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>Result of a refund attempt, so controllers can surface a clear Arabic message.</summary>
public sealed record RefundResult(bool Success, string Message, Refund? Refund = null, bool PendingApproval = false);

/// <summary>
/// Owns the invoice-balance invariant and the refund workflow. The paid amount of an invoice is
/// always <c>SUM(active payments) − SUM(approved refunds)</c>, derived from the ledgers rather than
/// incremented in place, so it can never drift after a refund, cancellation or failed write. The
/// refund itself is a new dated movement (never a deletion of an old payment) that also debits the
/// custody of the employee who performed it — all inside one transaction.
///
/// A refund above the configured direct ceiling does not apply immediately: it is stored as
/// <see cref="RefundStatus.PendingApproval"/> and touches neither the invoice nor the custody until a
/// SuperAdmin approves it (see <see cref="ApproveRefundAsync"/>).
/// </summary>
public class FinancialService
{
    private readonly ECertDbContext _db;
    private readonly FinancialLedgerService _ledger;
    private readonly SystemSettingsService _settings;
    private readonly PeriodLockService _periods;

    public FinancialService(ECertDbContext db, FinancialLedgerService ledger, SystemSettingsService settings,
        PeriodLockService periods)
    {
        _db = db;
        _ledger = ledger;
        _settings = settings;
        _periods = periods;
    }

    /// <summary>
    /// Recomputes <c>PaidAmount</c> and the status from the invoice's active payments and its
    /// approved refunds. A cancelled invoice keeps its cancelled status; refunds still awaiting
    /// approval are deliberately excluded so they cannot lower the balance early.
    /// </summary>
    public async Task RecomputeInvoiceAsync(Invoice invoice)
    {
        var paid = await _db.Payments
            .Where(p => p.InvoiceId == invoice.InvoiceId && !p.IsCancelled)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var refunded = await _db.Refunds
            .Where(r => r.InvoiceId == invoice.InvoiceId && r.Status == RefundStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        var net = paid - refunded;

        invoice.PaidAmount = net;
        invoice.Status = invoice.Status == "Cancelled"
            ? "Cancelled"
            : net >= invoice.TotalAmount && invoice.TotalAmount > 0 ? "Paid"
            : net > 0 ? "PartiallyPaid"
            : "Unpaid";
    }

    /// <summary>Total refunded on an invoice, counting only approved refunds.</summary>
    public async Task<decimal> GetRefundedAmountAsync(int invoiceId) =>
        await _db.Refunds.Where(r => r.InvoiceId == invoiceId && r.Status == RefundStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;

    /// <summary>Total pending-approval refunds on an invoice, so the ceiling can account for them.</summary>
    public async Task<decimal> GetPendingRefundAmountAsync(int invoiceId) =>
        await _db.Refunds.Where(r => r.InvoiceId == invoiceId && r.Status == RefundStatus.PendingApproval)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;

    /// <summary>
    /// Creates a refund against an invoice. The amount may not exceed the invoice's currently paid
    /// amount (minus any refunds already awaiting approval), and a reason is mandatory. A refund above
    /// the configured direct ceiling is created in <see cref="RefundStatus.PendingApproval"/>: it does
    /// not debit custody or change the invoice until approved. The caller must already be inside a
    /// transaction; this method saves but does not commit.
    /// </summary>
    public async Task<RefundResult> CreateRefundAsync(
        Invoice invoice,
        decimal amount,
        string reason,
        string refundMethod,
        string? receiptNumber,
        int performingUserId,
        string performingUserName,
        bool isSuperAdmin = false)
    {
        if (invoice.Status == "Cancelled")
            return new RefundResult(false, "لا يمكن استرداد رسوم من فاتورة ملغية.");

        if (await _periods.IsLockedAsync(DateTime.Now))
            return new RefundResult(false, (await _periods.BlockedMessageAsync(DateTime.Now))!);

        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0)
            return new RefundResult(false, "مبلغ الاسترداد يجب أن يكون أكبر من صفر.");

        if (string.IsNullOrWhiteSpace(reason))
            return new RefundResult(false, "سبب الاسترداد مطلوب.");

        // The refundable ceiling is the invoice's net paid amount (payments minus approved refunds),
        // reduced by refunds already pending approval so two pending refunds cannot together exceed it.
        await RecomputeInvoiceAsync(invoice);
        var pending = await GetPendingRefundAmountAsync(invoice.InvoiceId);
        var available = invoice.PaidAmount - pending;
        if (amount > available)
            return new RefundResult(false, $"مبلغ الاسترداد ({amount:N2}) أكبر من المتاح للاسترداد ({available:N2}).");

        // Where the money comes from. The refund is normally paid out of the employee's own custody,
        // and is refused when that custody cannot cover it — so a custody can never go negative. A
        // SuperAdmin whose own custody cannot cover it may draw the amount from the main fund instead
        // (the fund must cover it), so the SuperAdmin is never blocked and no custody is pushed below zero.
        var custody = await _ledger.GetCustodyBalanceAsync(performingUserId);
        var payFromFund = false;
        if (amount > custody)
        {
            if (!isSuperAdmin)
                return new RefundResult(false,
                    $"رصيد عهدتك ({custody:N2}) لا يكفي لصرف هذا الاسترداد ({amount:N2}). ورّد عهدتك أو تواصل مع مدير النظام.");

            var fund = await _ledger.GetMainFundBalanceAsync();
            if (amount > fund)
                return new RefundResult(false,
                    $"رصيد عهدتك ({custody:N2}) ورصيد الصندوق الرئيسي ({fund:N2}) لا يكفيان لصرف هذا الاسترداد ({amount:N2}).");
            payFromFund = true;
        }

        var requiresApproval = await _settings.RefundRequiresApprovalAsync(amount);

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
            PaidFromFund = payFromFund,
            Status = requiresApproval ? RefundStatus.PendingApproval : RefundStatus.Approved,
            ApprovedBy = requiresApproval ? null : performingUserName,
            ApprovedAt = requiresApproval ? null : DateTime.Now,
            CreatedAt = DateTime.Now
        };
        await _ledger.EnsureRefundPublicIdAsync(refund);
        _db.Refunds.Add(refund);
        await _db.SaveChangesAsync();

        if (requiresApproval)
        {
            // A large refund is only recorded; it moves no money until a SuperAdmin approves it.
            _ledger.AddAudit(performingUserName, FinancialActionTypes.Refund, amount,
                referenceType: nameof(Refund), referenceId: refund.RefundId, referenceNumber: refund.ReceiptNumber,
                details: $"طلب استرداد بانتظار الاعتماد على الفاتورة {invoice.InvoiceNumber}: {refund.Reason}");
            await _db.SaveChangesAsync();
            return new RefundResult(true,
                $"تم تسجيل طلب استرداد بقيمة {amount:N2} {await _settings.GetCurrencySymbolAsync()} وهو بانتظار اعتماد مدير النظام.", refund, PendingApproval: true);
        }

        ApplyRefundPayout(refund, performingUserId, performingUserName, invoice);

        await RecomputeInvoiceAsync(invoice);
        await _db.SaveChangesAsync();

        return new RefundResult(true, $"تم تسجيل استرداد بقيمة {amount:N2} {await _settings.GetCurrencySymbolAsync()} بنجاح.", refund);
    }

    /// <summary>
    /// Approves a refund that was waiting for approval: it now moves the money (out of the
    /// performing employee's custody, or out of the main fund when it was requested that way) and
    /// lowers the invoice balance, exactly as a direct refund would have. The caller must already be
    /// inside a transaction; this method saves but does not commit.
    /// </summary>
    public async Task<RefundResult> ApproveRefundAsync(Refund refund, int performingUserId, string approver)
    {
        if (refund.Status != RefundStatus.PendingApproval)
            return new RefundResult(false, "هذا الاسترداد ليس بانتظار الاعتماد.");

        var invoice = await _db.Invoices.FirstOrDefaultAsync(i => i.InvoiceId == refund.InvoiceId);
        if (invoice == null)
            return new RefundResult(false, "الفاتورة المرتبطة غير موجودة.");

        // Re-check the funding source at approval time: custody may have been handed over since the
        // request was created, so a request that no longer has cover must not go through. The approver
        // is always a SuperAdmin, so a shortfall may be drawn from the main fund (never a negative custody).
        var custody = await _ledger.GetCustodyBalanceAsync(performingUserId);
        if (refund.Amount > custody)
        {
            var fund = await _ledger.GetMainFundBalanceAsync();
            if (refund.Amount > fund)
                return new RefundResult(false,
                    $"رصيد العهدة ({custody:N2}) ورصيد الصندوق الرئيسي ({fund:N2}) لا يكفيان لاعتماد هذا الاسترداد ({refund.Amount:N2}).");
            refund.PaidFromFund = true;
        }

        refund.Status = RefundStatus.Approved;
        refund.ApprovedBy = approver;
        refund.ApprovedAt = DateTime.Now;

        ApplyRefundPayout(refund, performingUserId, approver, invoice);

        await RecomputeInvoiceAsync(invoice);
        await _db.SaveChangesAsync();

        return new RefundResult(true, $"تم اعتماد الاسترداد بقيمة {refund.Amount:N2} {await _settings.GetCurrencySymbolAsync()} بنجاح.", refund);
    }

    /// <summary>
    /// Moves the money for an approved refund and audits it: out of the employee's custody, or — when
    /// the refund is paid from the main fund — as a fund payout that reduces the fund balance (see
    /// <see cref="FinancialLedgerService.GetMainFundBalanceAsync"/>). A fund payout debits no custody,
    /// so a custody can never be pushed below zero by a refund.
    /// </summary>
    private void ApplyRefundPayout(Refund refund, int performingUserId, string actor, Invoice invoice)
    {
        var note = $"{(refund.PaidFromFund ? "استرداد من الصندوق" : "استرداد")} على الفاتورة {invoice.InvoiceNumber}: {refund.Reason}";
        if (refund.PaidFromFund)
        {
            _ledger.AddAudit(actor, FinancialActionTypes.Refund, refund.Amount,
                referenceType: nameof(Refund), referenceId: refund.RefundId,
                referenceNumber: refund.ReceiptNumber, details: note);
        }
        else
        {
            _ledger.AddRefundCustody(performingUserId, refund.Amount, actor, refund.RefundId,
                refund.ReceiptNumber, note);
        }
    }
}
