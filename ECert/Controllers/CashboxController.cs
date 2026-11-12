using System.Data;
using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// The main fund (الصندوق الرئيسي) — SuperAdmin only.
///
/// Money reaches the fund through exactly one path: an employee requests a handover (توريد) from
/// <c>/Custody</c> and a SuperAdmin approves it. The legacy per-course "transfer" action is retired;
/// this controller only reads the fund's movements (handovers in, withdrawals out) and lets the
/// SuperAdmin withdraw from the fund. Every balance is derived from the ledgers at query time —
/// there is no mutable stored balance.
/// </summary>
[Authorize]
public class CashboxController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly FinancialLedgerService _ledger;
    private readonly PeriodLockService _periods;

    public CashboxController(ECertDbContext db, AuditLogService audit, FinancialLedgerService ledger,
        PeriodLockService periods)
    {
        _db = db;
        _audit = audit;
        _ledger = ledger;
        _periods = periods;
    }

    private bool IsSuperAdmin => User.IsSuperAdmin();

    /// <summary>منظر الصندوق الشامل — إجمالي الصندوق، التوريدات (من العهد)، والسحوبات.</summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (!IsSuperAdmin) return Forbid();

        // الترحيلات تُقرأ عبر ADO.NET لتجاوز edge case في EF مع Pomelo.
        var transfers = new List<CashboxTransfer>();
        var withdrawals = new List<CashboxWithdrawal>();
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
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
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT `Id`,`Amount`,`Reason`,`CreatedBy`,`CreatedAt` FROM `CashboxWithdrawals` ORDER BY `CreatedAt` DESC";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    withdrawals.Add(new CashboxWithdrawal
                    {
                        Id = r.GetInt32(0),
                        Amount = r.GetDecimal(1),
                        Reason = r.GetString(2),
                        CreatedBy = r.GetString(3),
                        CreatedAt = r.GetDateTime(4)
                    });
            }
        }
        finally { if (shouldClose) await conn.CloseAsync(); }

        decimal totalTransferred = transfers.Sum(t => t.Amount);
        decimal totalWithdrawn = withdrawals.Sum(w => w.Amount);
        decimal totalCollected = await _db.Payments.Where(p => !p.IsCancelled)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
        decimal custodyTotal = await _ledger.GetTotalCustodyAsync();

        ViewBag.Transfers = transfers;
        ViewBag.Withdrawals = withdrawals;
        ViewBag.TotalCollected = totalCollected;
        ViewBag.TotalTransferred = totalTransferred;
        ViewBag.TotalWithdrawn = totalWithdrawn;
        ViewBag.FundRemaining = totalTransferred - totalWithdrawn;
        ViewBag.CustodyTotal = custodyTotal;
        ViewBag.Courses = await _db.Courses.OrderBy(c => c.CourseName).ToListAsync();
        ViewBag.IsSuperAdmin = IsSuperAdmin;
        return View();
    }

    /// <summary>سحب من الصندوق — SuperAdmin فقط، مع احترام إقفال الفترة المالية.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(decimal amount, string reason)
    {
        if (!IsSuperAdmin) return Forbid();

        if (amount <= 0)
        {
            TempData["Error"] = "مبلغ السحب يجب أن يكون أكبر من صفر.";
            return RedirectToAction("Index");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "يجب ذكر سبب السحب.";
            return RedirectToAction("Index");
        }
        if (await _periods.IsLockedAsync(DateTime.Now))
        {
            TempData["Error"] = await _periods.BlockedMessageAsync(DateTime.Now);
            return RedirectToAction("Index");
        }

        // الصندوق المتاح = إجمالي التوريدات − إجمالي السحوبات
        decimal totalTransferred = await _db.CashboxTransfers.SumAsync(t => (decimal?)t.Amount) ?? 0m;
        decimal totalWithdrawn = await _db.CashboxWithdrawals.SumAsync(w => (decimal?)w.Amount) ?? 0m;

        var available = totalTransferred - totalWithdrawn;
        if (amount > available)
        {
            TempData["Error"] = $"مبلغ السحب ({amount:N2}) أكبر من الصندوق المتاح ({available:N2}).";
            return RedirectToAction("Index");
        }

        var actor = User.Identity?.Name ?? CashboxRoles.SystemManager;
        var entry = new CashboxWithdrawal
        {
            Amount = amount,
            Reason = reason.Trim(),
            CreatedBy = actor,
            CreatedAt = DateTime.Now
        };
        _db.CashboxWithdrawals.Add(entry);
        await _db.SaveChangesAsync();

        _ledger.AddAudit(actor, FinancialActionTypes.Withdraw, amount,
            referenceType: "CashboxWithdrawal", referenceId: entry.Id,
            details: $"سحب من الصندوق — {reason}");
        await _db.SaveChangesAsync();

        await _audit.LogAsync(actor, "Withdraw", "Cashbox", entry.Id, null,
            $"سحب {amount:N2} ريال — {reason}");

        TempData["Success"] = $"تم تسجيل سحب {amount:N2} ريال من الصندوق.";
        return RedirectToAction("Index");
    }
}
