using System.Data;
using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// The main fund (الصندوق الرئيسي).
///
/// Money reaches the fund through exactly one path: an employee requests a handover (توريد) from
/// <c>/Custody</c> and someone holding <c>approve-custody</c> approves it. Money leaves the fund
/// through an approved expense (مصروف معتمد) or a refund paid straight from the fund — the standalone
/// withdrawal feature is retired. This controller reads the fund's movements (handovers in, expenses
/// and fund refunds out) for anyone holding <c>view-cashbox</c> or <c>manage-cashbox</c>. Every
/// balance is derived from the ledgers at query time — there is no mutable stored balance.
/// </summary>
[Authorize]
public class CashboxController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly FinancialLedgerService _ledger;

    public CashboxController(ECertDbContext db, AuditLogService audit, FinancialLedgerService ledger)
    {
        _db = db;
        _audit = audit;
        _ledger = ledger;
    }

    private bool CanView => User.CanAccessSection("Cashbox");

    /// <summary>منظر الصندوق الشامل — إجمالي الصندوق، التوريدات (من العهد)، والمصروفات المعتمدة.</summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (!CanView) return Forbid();

        // الترحيلات تُقرأ عبر ADO.NET لتجاوز edge case في EF مع Pomelo.
        var transfers = new List<CashboxTransfer>();
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT `Id`,`CourseId`,`Amount`,COALESCE(`Note`,''),`CreatedBy`,`CreatedAt` FROM `CashboxTransfers` ORDER BY `CreatedAt` DESC";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                transfers.Add(new CashboxTransfer
                {
                    Id = r.GetInt32(0),
                    CourseId = r.IsDBNull(1) ? null : r.GetInt32(1),
                    Amount = r.GetDecimal(2),
                    Note = r.GetString(3),
                    CreatedBy = r.GetString(4),
                    CreatedAt = r.GetDateTime(5)
                });
        }
        finally { if (shouldClose) await conn.CloseAsync(); }

        decimal totalTransferred = transfers.Sum(t => t.Amount);
        decimal totalCollected = await _db.Payments.Where(p => !p.IsCancelled)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
        decimal custodyTotal = await _ledger.GetTotalCustodyAsync();

        // Outflows from the fund: approved expenses and refunds paid straight from the fund.
        var approvedExpenses = await _db.Expenses
            .Include(e => e.Category)
            .Where(e => e.Status == ExpenseStatus.Approved)
            .OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.ExpenseId)
            .Take(200)
            .ToListAsync();
        var fundRefunds = await _db.Refunds
            .Where(r => r.PaidFromFund && r.Status == RefundStatus.Approved)
            .OrderByDescending(r => r.RefundDate).ThenByDescending(r => r.RefundId)
            .Take(200)
            .ToListAsync();
        decimal totalExpenses = await _ledger.GetApprovedExpensesTotalAsync();
        decimal totalFundRefunds = fundRefunds.Sum(r => r.Amount);

        ViewBag.Transfers = transfers;
        ViewBag.TotalCollected = totalCollected;
        ViewBag.TotalTransferred = totalTransferred;
        ViewBag.TotalExpenses = totalExpenses;
        ViewBag.TotalFundRefunds = totalFundRefunds;
        ViewBag.FundBalance = await _ledger.GetMainFundBalanceAsync();
        ViewBag.Expenses = approvedExpenses;
        ViewBag.FundRefunds = fundRefunds;
        ViewBag.CustodyTotal = custodyTotal;
        ViewBag.Courses = await _db.Courses.OrderBy(c => c.CourseName).ToListAsync();
        return View();
    }
}
