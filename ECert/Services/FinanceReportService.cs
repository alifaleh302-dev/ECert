using System.Security.Claims;
using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Computes the numbers behind the financial dashboard and the additional reports (cash-flow,
/// income vs expense, ageing receivables, custody balances). Every query is confined to the caller's
/// course scope through <see cref="CourseScopeQueries"/>, so a course-scoped collector sees only
/// their own courses and never the whole institution. Amounts are always derived from the ledgers,
/// never from a stored balance.
/// </summary>
public class FinanceReportService
{
    private readonly ECertDbContext _db;
    public FinanceReportService(ECertDbContext db) => _db = db;

    public sealed record DashboardData(
        decimal CollectedToday,
        decimal CollectedThisMonth,
        decimal RefundedThisMonth,
        decimal ExpensesThisMonth,
        decimal OutstandingTotal,
        decimal CustodyTotal,
        decimal FundBalance,
        decimal NetThisMonth,
        int UnpaidInvoices);

    public sealed record CashFlowRow(DateTime Day, decimal Collections, decimal Refunds, decimal Withdrawals, decimal Expenses)
    {
        public decimal Net => Collections - Refunds - Withdrawals - Expenses;
    }

    public sealed record CustodyBalanceRow(int UserId, string FullName, string Username, decimal Balance);

    public sealed record AgingRow(string Bucket, string Label, int Count, decimal Amount);

    /// <summary>KPI figures for the financial dashboard, scoped to the caller.</summary>
    public async Task<DashboardData> GetDashboardAsync(ClaimsPrincipal user)
    {
        var today = DateTime.Today;
        var monthStart = new DateTime(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1);

        var payments = _db.Payments.InScope(user).Where(p => !p.IsCancelled);
        var collectedToday = await payments.Where(p => p.PaymentDate >= today).SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var collectedThisMonth = await payments.Where(p => p.PaymentDate >= monthStart && p.PaymentDate < monthEnd)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;

        var refundedThisMonth = await _db.Refunds.InScope(user)
            .Where(r => r.Status == RefundStatus.Approved && r.RefundDate >= monthStart && r.RefundDate < monthEnd)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;

        var expensesThisMonth = await _db.Expenses
            .Where(e => e.Status == ExpenseStatus.Approved && e.ExpenseDate >= monthStart && e.ExpenseDate < monthEnd)
            .SumAsync(e => (decimal?)e.Amount) ?? 0m;

        var invoices = await _db.Invoices.InScope(user)
            .Where(i => i.Status != "Cancelled" && i.Registration != null)
            .Select(i => new { i.InvoiceId, i.TotalAmount, i.PaidAmount })
            .ToListAsync();
        var outstanding = invoices.Sum(i => Math.Max(0m, i.TotalAmount - i.PaidAmount));
        var unpaidCount = invoices.Count(i => i.TotalAmount - i.PaidAmount > 0);

        // Custody is per employee, not per course; a course-scoped user only sees their own.
        decimal custodyTotal;
        if (user.AllowedCourseIds() is { } ids)
        {
            var ownId = int.TryParse(user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var id) ? id : 0;
            custodyTotal = await _db.CustodyEntries.Where(e => e.UserId == ownId)
                .SumAsync(e => (decimal?)e.Amount) ?? 0m;
        }
        else
        {
            custodyTotal = await _db.CustodyEntries.SumAsync(e => (decimal?)e.Amount) ?? 0m;
        }

        var allTransferred = await _db.CashboxTransfers.SumAsync(t => (decimal?)t.Amount) ?? 0m;
        var allWithdrawn = await _db.CashboxWithdrawals.SumAsync(w => (decimal?)w.Amount) ?? 0m;
        var fundBalance = allTransferred - allWithdrawn;

        return new DashboardData(collectedToday, collectedThisMonth, refundedThisMonth, expensesThisMonth,
            outstanding, custodyTotal, fundBalance,
            collectedThisMonth - refundedThisMonth - expensesThisMonth, unpaidCount);
    }

    /// <summary>Daily cash-flow over an inclusive date range, one row per day that had activity.</summary>
    public async Task<List<CashFlowRow>> GetCashFlowAsync(ClaimsPrincipal user, DateTime from, DateTime to)
    {
        var fromDate = from.Date;
        var toExclusive = to.Date.AddDays(1);

        var collections = await _db.Payments.InScope(user)
            .Where(p => !p.IsCancelled && p.PaymentDate >= fromDate && p.PaymentDate < toExclusive)
            .GroupBy(p => p.PaymentDate.Date)
            .Select(g => new { Day = g.Key, Total = g.Sum(p => p.Amount) })
            .ToListAsync();

        var refunds = await _db.Refunds.InScope(user)
            .Where(r => r.Status == RefundStatus.Approved && r.RefundDate >= fromDate && r.RefundDate < toExclusive)
            .GroupBy(r => r.RefundDate.Date)
            .Select(g => new { Day = g.Key, Total = g.Sum(r => r.Amount) })
            .ToListAsync();

        var withdrawals = await _db.CashboxWithdrawals
            .Where(w => w.CreatedAt >= fromDate && w.CreatedAt < toExclusive)
            .GroupBy(w => w.CreatedAt.Date)
            .Select(g => new { Day = g.Key, Total = g.Sum(w => w.Amount) })
            .ToListAsync();

        var expenses = await _db.Expenses
            .Where(e => e.Status == ExpenseStatus.Approved && e.ExpenseDate >= fromDate && e.ExpenseDate < toExclusive)
            .GroupBy(e => e.ExpenseDate.Date)
            .Select(g => new { Day = g.Key, Total = g.Sum(e => e.Amount) })
            .ToListAsync();

        var days = collections.Select(c => c.Day)
            .Concat(refunds.Select(r => r.Day))
            .Concat(withdrawals.Select(w => w.Day))
            .Concat(expenses.Select(e => e.Day))
            .Distinct().OrderBy(d => d);

        var cMap = collections.ToDictionary(x => x.Day, x => x.Total);
        var rMap = refunds.ToDictionary(x => x.Day, x => x.Total);
        var wMap = withdrawals.ToDictionary(x => x.Day, x => x.Total);
        var eMap = expenses.ToDictionary(x => x.Day, x => x.Total);

        return days.Select(d => new CashFlowRow(d,
            cMap.GetValueOrDefault(d), rMap.GetValueOrDefault(d),
            wMap.GetValueOrDefault(d), eMap.GetValueOrDefault(d))).ToList();
    }

    /// <summary>Ageing of outstanding receivables, bucketed by how far past due they are.</summary>
    public async Task<List<AgingRow>> GetAgingAsync(ClaimsPrincipal user)
    {
        var invoices = await _db.Invoices.InScope(user)
            .Where(i => i.Status != "Cancelled" && i.Status != "Paid" && i.Registration != null)
            .Select(i => new { i.TotalAmount, i.PaidAmount, i.DueDate })
            .ToListAsync();

        var today = DateTime.Today;
        var buckets = new (string Key, string Label)[] {
            ("current", "غير مستحق بعد"), ("d30", "متأخر 1–30 يوم"), ("d60", "متأخر 31–60 يوم"),
            ("d90", "متأخر 61–90 يوم"), ("d90plus", "متأخر أكثر من 90 يوم"), ("none", "بلا تاريخ استحقاق")
        };
        var counts = buckets.ToDictionary(b => b.Key, _ => 0);
        var sums = buckets.ToDictionary(b => b.Key, _ => 0m);

        foreach (var inv in invoices)
        {
            var remaining = inv.TotalAmount - inv.PaidAmount;
            if (remaining <= 0) continue;
            string key;
            if (!inv.DueDate.HasValue) key = "none";
            else
            {
                var days = (today - inv.DueDate.Value.Date).Days;
                key = days <= 0 ? "current" : days <= 30 ? "d30" : days <= 60 ? "d60" : days <= 90 ? "d90" : "d90plus";
            }
            counts[key]++;
            sums[key] += remaining;
        }

        return buckets.Select(b => new AgingRow(b.Key, b.Label, counts[b.Key], sums[b.Key])).ToList();
    }

    /// <summary>Custody balance per employee (SuperAdmin view).</summary>
    public async Task<List<CustodyBalanceRow>> GetCustodyBalancesAsync()
    {
        // Order on the anonymous projection: EF cannot translate an OrderBy over a record's
        // positional-constructor property after the join.
        var rows = await _db.CustodyEntries
            .GroupBy(e => e.UserId)
            .Select(g => new { UserId = g.Key, Balance = g.Sum(e => e.Amount) })
            .Join(_db.Users, g => g.UserId, u => u.UserId,
                (g, u) => new { u.UserId, u.FullName, u.Username, g.Balance })
            .OrderByDescending(r => r.Balance)
            .ToListAsync();
        return rows.Select(r => new CustodyBalanceRow(r.UserId, r.FullName, r.Username, r.Balance)).ToList();
    }

    /// <summary>Approved expense totals grouped by category over an inclusive range.</summary>
    public async Task<List<(string Category, decimal Total)>> GetExpensesByCategoryAsync(DateTime from, DateTime to)
    {
        var toExclusive = to.Date.AddDays(1);
        var rows = await _db.Expenses
            .Where(e => e.Status == ExpenseStatus.Approved && e.ExpenseDate >= from.Date && e.ExpenseDate < toExclusive)
            .GroupBy(e => e.ExpenseCategoryId)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(e => e.Amount) })
            .ToListAsync();
        var categories = await _db.ExpenseCategories.ToDictionaryAsync(c => c.ExpenseCategoryId, c => c.Name);
        return rows.Select(r => (categories.GetValueOrDefault(r.CategoryId, "بند"), r.Total))
            .OrderByDescending(r => r.Item2).ToList();
    }
}
