using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Single source of truth for an invoice's totals and status. The total is the sum of its priced
/// lines minus the exemption; the paid amount is derived from the ledger (active payments minus
/// approved refunds). A zero-total invoice is "Paid" because nothing is owed, and stays that way
/// when it is recalculated — the bug the old duplicated recompute methods had.
/// </summary>
public class InvoiceBalanceService
{
    private readonly ECertDbContext _db;

    public InvoiceBalanceService(ECertDbContext db) => _db = db;

    public async Task RecalculateAsync(Invoice invoice)
    {
        var itemsTotal = await _db.InvoiceItems
            .Where(i => i.InvoiceId == invoice.InvoiceId)
            .SumAsync(i => (decimal?)i.Amount) ?? 0m;

        invoice.OriginalAmount = itemsTotal;
        invoice.TotalAmount = Math.Max(0m, itemsTotal - invoice.ExemptionAmount);

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
            : invoice.TotalAmount <= 0 ? "Paid"
            : net >= invoice.TotalAmount ? "Paid"
            : net > 0 ? "PartiallyPaid"
            : "Unpaid";
    }

    public async Task<decimal> GetItemsTotalAsync(int invoiceId) =>
        await _db.InvoiceItems.Where(i => i.InvoiceId == invoiceId)
            .SumAsync(i => (decimal?)i.Amount) ?? 0m;
}
