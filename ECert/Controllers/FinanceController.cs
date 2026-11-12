using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// The financial dashboard (لوحة المؤشرات المالية): the institution's headline figures — collected
/// today/this month, outstanding, refunds, expenses, custody and fund balance — plus the latest
/// movements. Everything is confined to the caller's course scope through
/// <see cref="FinanceReportService"/>.
/// </summary>
[Authorize]
public class FinanceController : Controller
{
    private readonly ECertDbContext _db;
    private readonly FinanceReportService _reports;

    public FinanceController(ECertDbContext db, FinanceReportService reports)
    {
        _db = db;
        _reports = reports;
    }

    public async Task<IActionResult> Index()
    {
        if (!User.HasPermission(AdminPermissions.ViewFinanceDashboard)) return Forbid();

        ViewBag.Data = await _reports.GetDashboardAsync(User);
        ViewBag.Aging = await _reports.GetAgingAsync(User);
        ViewBag.CashFlow = (await _reports.GetCashFlowAsync(User, DateTime.Today.AddDays(-13), DateTime.Today))
            .OrderByDescending(r => r.Day).ToList();

        ViewBag.RecentPayments = await _db.Payments.InScope(User)
            .Include(p => p.Invoice)
            .Where(p => !p.IsCancelled)
            .OrderByDescending(p => p.PaymentDate).ThenByDescending(p => p.PaymentId)
            .Take(8).ToListAsync();

        ViewBag.RecentRefunds = await _db.Refunds.InScope(User)
            .Include(r => r.Invoice)
            .Where(r => r.Status == RefundStatus.Approved)
            .OrderByDescending(r => r.RefundDate).ThenByDescending(r => r.RefundId)
            .Take(8).ToListAsync();

        return View();
    }
}
