using System.Data;
using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services;
using ECert.Services.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class ReportsController : Controller
{
    private readonly ECertDbContext _db;
    private readonly FinanceReportService _reports;
    private readonly CourseRevenueService _revenue;
    private readonly CashReconciliationService _reconciliation;

    public ReportsController(ECertDbContext db, FinanceReportService reports, CourseRevenueService revenue,
        CashReconciliationService reconciliation)
    {
        _db = db;
        _reports = reports;
        _revenue = revenue;
        _reconciliation = reconciliation;
    }

    public IActionResult Index() => RedirectToAction("CourseRevenue");

    public async Task<IActionResult> CourseRevenue(int? categoryId, int? courseId, string? status, DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ViewReports)) return Forbid();

        var filters = new CourseRevenueService.Filters(categoryId, courseId, status, from, to);
        var report = await _revenue.GetReportAsync(User, filters);

        ViewBag.Data = report.Rows;
        ViewBag.Summary = report.Summary;
        ViewBag.Categories = await _db.Categories.OrderBy(c => c.CategoryName).ToListAsync();
        ViewBag.Courses = await _db.Courses.InScope(User).OrderBy(c => c.CourseName).ToListAsync();
        ViewBag.CategoryId = categoryId;
        ViewBag.CourseId = courseId;
        ViewBag.Status = status;
        ViewBag.From = from?.ToString("yyyy-MM-dd");
        ViewBag.To = to?.ToString("yyyy-MM-dd");
        return View();
    }

    /// <summary>Per-course drill-down: trainee collections plus breakdowns by employee and payment method.</summary>
    public async Task<IActionResult> CourseRevenueDetail(int courseId, DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ViewReports)) return Forbid();

        var detail = await _revenue.GetDetailAsync(User, courseId, from, to);
        if (detail == null) return NotFound();
        ViewBag.From = from?.ToString("yyyy-MM-dd");
        ViewBag.To = to?.ToString("yyyy-MM-dd");
        return View(detail);
    }

    /// <summary>Print-friendly version of the per-course drill-down (used to produce a PDF).</summary>
    public async Task<IActionResult> PrintCourseRevenueDetail(int courseId, DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ViewReports)) return Forbid();

        var detail = await _revenue.GetDetailAsync(User, courseId, from, to);
        if (detail == null) return NotFound();
        ViewBag.From = from?.ToString("yyyy-MM-dd");
        ViewBag.To = to?.ToString("yyyy-MM-dd");
        return View(detail);
    }

    public async Task<IActionResult> ExportCourseRevenueXlsx(int? categoryId, int? courseId, string? status, DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ViewReports)) return Forbid();

        var filters = new CourseRevenueService.Filters(categoryId, courseId, status, from, to);
        var (rows, summary) = await _revenue.GetExportAsync(User, filters);

        using var export = new ExcelExport("إيرادات الدورات",
            "الدورة", "الفئة", "حالة الدورة", "عدد المسجلين", "الرسوم الأصلية", "الإعفاءات", "الإيراد المتوقع",
            "المدفوع", "المسترد", "صافي الإيراد الفعلي", "المتبقي", "نسبة التحصيل %",
            "سداد كامل", "جزئي", "غير مسدد", "نقدي", "محافظ إلكترونية");

        foreach (var r in rows)
        {
            export.WriteRow(
                r.CourseName, r.CategoryName, ExcelLabels.CourseStatus(r.Status), r.Registrations,
                r.Original, r.Exemptions, r.Expected, r.Paid, r.Refunds, r.NetPaid, r.Remaining,
                r.CollectionPercent, r.FullPaid, r.Partial, r.Unpaid, r.Cash, r.Wallet);
        }

        export.WriteRow("الإجمالي", "", "", "",
            rows.Sum(r => r.Original), rows.Sum(r => r.Exemptions), summary.Expected,
            rows.Sum(r => r.Paid), rows.Sum(r => r.Refunds), summary.NetPaid, summary.Remaining,
            summary.CollectionPercent, "", "", "", summary.Cash, summary.Wallet);

        return export.Build("course_revenue");
    }

    /// <summary>Daily cash-flow report (التحصيل والاسترداد والسحب والمصروفات) over a date range.</summary>
    public async Task<IActionResult> CashFlow(DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ViewReports)) return Forbid();

        var toDate = (to ?? DateTime.Today).Date;
        var fromDate = (from ?? toDate.AddDays(-29)).Date;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        var rows = await _reports.GetCashFlowAsync(User, fromDate, toDate);
        ViewBag.From = fromDate.ToString("yyyy-MM-dd");
        ViewBag.To = toDate.ToString("yyyy-MM-dd");
        ViewBag.Rows = rows;
        ViewBag.Collections = rows.Sum(r => r.Collections);
        ViewBag.Refunds = rows.Sum(r => r.Refunds);
        ViewBag.Withdrawals = rows.Sum(r => r.Withdrawals);
        ViewBag.Expenses = rows.Sum(r => r.Expenses);
        ViewBag.Net = rows.Sum(r => r.Net);
        return View();
    }

    public async Task<IActionResult> ExportCashFlowXlsx(DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ViewReports)) return Forbid();

        var toDate = (to ?? DateTime.Today).Date;
        var fromDate = (from ?? toDate.AddDays(-29)).Date;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        var rows = await _reports.GetCashFlowAsync(User, fromDate, toDate);
        using var export = new ExcelExport("التدفق النقدي",
            "التاريخ", "التحصيل", "الاسترداد", "السحب", "المصروفات", "الصافي");
        foreach (var r in rows)
            export.WriteRow(r.Day.ToString("yyyy-MM-dd"), r.Collections, r.Refunds, r.Withdrawals, r.Expenses, r.Net);
        export.WriteRow("الإجمالي", rows.Sum(r => r.Collections), rows.Sum(r => r.Refunds),
            rows.Sum(r => r.Withdrawals), rows.Sum(r => r.Expenses), rows.Sum(r => r.Net));
        return export.Build("cash_flow");
    }

    /// <summary>Income statement (قائمة الدخل): revenue recognised minus approved expenses.</summary>
    public async Task<IActionResult> IncomeStatement(DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ViewReports)) return Forbid();

        var toDate = (to ?? DateTime.Today).Date;
        var fromDate = (from ?? new DateTime(toDate.Year, 1, 1)).Date;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);
        var toExclusive = toDate.AddDays(1);

        var collections = await _db.Payments.InScope(User)
            .Where(p => !p.IsCancelled && p.PaymentDate >= fromDate && p.PaymentDate < toExclusive)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var refunds = await _db.Refunds.InScope(User)
            .Where(r => r.Status == RefundStatus.Approved && r.RefundDate >= fromDate && r.RefundDate < toExclusive)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        var expenses = await _reports.GetExpensesByCategoryAsync(fromDate, toDate);
        var expenseTotal = expenses.Sum(e => e.Total);

        ViewBag.From = fromDate.ToString("yyyy-MM-dd");
        ViewBag.To = toDate.ToString("yyyy-MM-dd");
        ViewBag.Collections = collections;
        ViewBag.Refunds = refunds;
        ViewBag.NetRevenue = collections - refunds;
        ViewBag.Expenses = expenses;
        ViewBag.ExpenseTotal = expenseTotal;
        ViewBag.NetIncome = collections - refunds - expenseTotal;
        return View();
    }

    public async Task<IActionResult> ExportIncomeStatementXlsx(DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ViewReports)) return Forbid();

        var toDate = (to ?? DateTime.Today).Date;
        var fromDate = (from ?? new DateTime(toDate.Year, 1, 1)).Date;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);
        var toExclusive = toDate.AddDays(1);

        var collections = await _db.Payments.InScope(User)
            .Where(p => !p.IsCancelled && p.PaymentDate >= fromDate && p.PaymentDate < toExclusive)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var refunds = await _db.Refunds.InScope(User)
            .Where(r => r.Status == RefundStatus.Approved && r.RefundDate >= fromDate && r.RefundDate < toExclusive)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        var expenses = await _reports.GetExpensesByCategoryAsync(fromDate, toDate);

        using var export = new ExcelExport("قائمة الدخل", "البند", "المبلغ");
        export.WriteRow("التحصيلات", collections);
        export.WriteRow("الاستردادات", -refunds);
        export.WriteRow("صافي الإيراد", collections - refunds);
        foreach (var e in expenses) export.WriteRow($"مصروف — {e.Category}", -e.Total);
        export.WriteRow("إجمالي المصروفات", -expenses.Sum(e => e.Total));
        export.WriteRow("صافي الدخل", collections - refunds - expenses.Sum(e => e.Total));
        return export.Build("income_statement");
    }

    /// <summary>Ageing of outstanding receivables by how far past due they are.</summary>
    public async Task<IActionResult> Aging()
    {
        if (!User.HasPermission(AdminPermissions.ViewReceivables)) return Forbid();

        ViewBag.Rows = await _reports.GetAgingAsync(User);
        ViewBag.Total = (await _reports.GetAgingAsync(User)).Sum(r => r.Amount);
        return View();
    }

    /// <summary>Cash reconciliation / Z-report for a single day or a range.</summary>
    public async Task<IActionResult> Reconciliation(DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ViewFinancialAudit)) return Forbid();

        var toDate = (to ?? DateTime.Today).Date;
        var fromDate = (from ?? toDate).Date;
        if (fromDate > toDate) (fromDate, toDate) = (toDate, fromDate);

        ViewBag.From = fromDate.ToString("yyyy-MM-dd");
        ViewBag.To = toDate.ToString("yyyy-MM-dd");
        ViewBag.Result = await _reconciliation.ReconcileAsync(fromDate, toDate);
        ViewBag.Custody = await _reports.GetCustodyBalancesAsync();
        return View();
    }
}
