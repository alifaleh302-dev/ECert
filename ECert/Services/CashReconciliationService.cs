using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Daily/periodic cash reconciliation (المطابقة النقدية / تقرير Z). It compares the cash the system
/// says is on hand against the cash the movements imply, and reports the difference so a day can be
/// closed with confidence. Both sides are derived from the same ledgers:
///
///   System balance  = main-fund balance (handovers − expenses − fund refunds) + custody.
///   Movement balance = collections − approved refunds − expenses.
///
/// They agree when every collection is either still in an employee's custody or already handed over
/// to the fund, every refund has left custody or the fund, and every expense was paid from the fund.
/// The standalone withdrawal feature is retired, so it no longer appears here.
/// </summary>
public class CashReconciliationService
{
    private readonly ECertDbContext _db;
    public CashReconciliationService(ECertDbContext db) => _db = db;

    public sealed record ReconciliationResult(
        DateTime From,
        DateTime To,
        decimal TotalCollected,
        decimal TotalRefunded,
        decimal TotalTransferred,
        decimal TotalExpenses,
        decimal TotalRefundedFromFund,
        decimal FundBalance,
        decimal CustodyBalance,
        decimal SystemBalance,
        decimal MovementBalance)
    {
        public decimal Difference => SystemBalance - MovementBalance;
        public bool IsBalanced => Difference == 0m;
    }

    /// <summary>
    /// Reconciles the cash on hand against the movements between <paramref name="from"/> and
    /// <paramref name="to"/> (inclusive). The system balance is as of the end of <paramref name="to"/>.
    /// </summary>
    public async Task<ReconciliationResult> ReconcileAsync(DateTime from, DateTime to)
    {
        var fromDate = from.Date;
        var toDate = to.Date;
        var toExclusive = toDate.AddDays(1);

        var totalCollected = await _db.Payments
            .Where(p => !p.IsCancelled && p.PaymentDate >= fromDate && p.PaymentDate < toExclusive)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;

        var totalRefunded = await _db.Refunds
            .Where(r => r.Status == Models.RefundStatus.Approved && r.RefundDate >= fromDate && r.RefundDate < toExclusive)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;

        var totalTransferred = await _db.CashboxTransfers
            .Where(t => t.CreatedAt >= fromDate && t.CreatedAt < toExclusive)
            .SumAsync(t => (decimal?)t.Amount) ?? 0m;

        var totalExpenses = await _db.Expenses
            .Where(e => e.Status == Models.ExpenseStatus.Approved && e.ExpenseDate >= fromDate && e.ExpenseDate < toExclusive)
            .SumAsync(e => (decimal?)e.Amount) ?? 0m;

        // Balances as of now: the fund is everything handed over, minus approved expenses, minus
        // refunds paid straight out of the fund; custody is the signed sum of every custody entry.
        var allTransferred = await _db.CashboxTransfers.SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var refundedFromFund = await _db.Refunds
            .Where(r => r.PaidFromFund && r.Status == Models.RefundStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        var allExpenses = await _db.Expenses
            .Where(e => e.Status == Models.ExpenseStatus.Approved)
            .SumAsync(e => (decimal?)e.Amount) ?? 0m;
        var custody = await _db.CustodyEntries.SumAsync(e => (decimal?)e.Amount) ?? 0m;

        var fundBalance = allTransferred - refundedFromFund - allExpenses;
        var systemBalance = fundBalance + custody;
        var movementBalance = totalCollected - totalRefunded - totalExpenses;

        return new ReconciliationResult(fromDate, toDate, totalCollected, totalRefunded,
            totalTransferred, totalExpenses, refundedFromFund, fundBalance, custody,
            systemBalance, movementBalance);
    }
}
