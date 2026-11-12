using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Installment plans (الأقساط والخطط): build a dated schedule for an invoice, view the schedule with
/// each installment's derived status (paid/partial/late/upcoming) and see which installments are
/// overdue. The plan carries no balance — status is always derived from the real payments.
/// </summary>
[Authorize]
public class InstallmentsController : Controller
{
    private readonly ECertDbContext _db;
    private readonly InstallmentService _installments;
    private readonly AuditLogService _audit;

    public InstallmentsController(ECertDbContext db, InstallmentService installments, AuditLogService audit)
    {
        _db = db;
        _installments = installments;
        _audit = audit;
    }

    private string Actor => User.Identity?.Name ?? "System";

    public async Task<IActionResult> Index()
    {
        if (!User.HasPermission(AdminPermissions.ManageInstallments)) return Forbid();

        var today = DateTime.Today;
        var plans = await _db.InstallmentPlans
            .Include(p => p.Installments)
            .Include(p => p.Invoice).ThenInclude(i => i!.Registration).ThenInclude(r => r!.Course)
            .InScope(User)
            .OrderByDescending(p => p.CreatedAt)
            .Take(300)
            .ToListAsync();

        var planIds = plans.Select(p => p.InvoiceId).ToList();
        var paidByInvoice = await _db.Payments
            .Where(p => !p.IsCancelled && planIds.Contains(p.InvoiceId))
            .GroupBy(p => p.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Total = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Total);
        var refundByInvoice = await _db.Refunds
            .Where(r => r.Status == RefundStatus.Approved && planIds.Contains(r.InvoiceId))
            .GroupBy(r => r.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Total = g.Sum(r => r.Amount) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Total);

        var rows = plans.Select(plan =>
        {
            var netPaid = paidByInvoice.GetValueOrDefault(plan.InvoiceId) - refundByInvoice.GetValueOrDefault(plan.InvoiceId);
            var views = InstallmentService.Allocate(plan.Installments, netPaid, today);
            return new InstallmentService.PlanRow(plan, views,
                views.Count(v => v.Status == "Paid"),
                views.Count(v => v.Status == "Late"),
                views.Sum(v => v.Remaining));
        }).ToList();

        ViewBag.Rows = rows;
        return View();
    }

    public async Task<IActionResult> Create(int invoiceId)
    {
        if (!User.HasPermission(AdminPermissions.ManageInstallments)) return Forbid();

        var invoice = await _db.Invoices
            .Include(i => i.Registration).ThenInclude(r => r!.Course)
            .FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);
        if (invoice == null) return NotFound();
        if (!User.CanAccessCourse(invoice.Registration?.CourseId)) return Forbid();

        ViewBag.Invoice = invoice;
        var plan = await _db.InstallmentPlans.Include(p => p.Installments)
            .FirstOrDefaultAsync(p => p.InvoiceId == invoiceId);
        ViewBag.Plan = plan;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int invoiceId, List<decimal> amount, List<DateTime> dueDate, string? note)
    {
        if (!User.HasPermission(AdminPermissions.ManageInstallments)) return Forbid();

        var invoice = await _db.Invoices.Include(i => i.Registration)
            .FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);
        if (invoice == null) return NotFound();
        if (!User.CanAccessCourse(invoice.Registration?.CourseId)) return Forbid();

        var rows = new List<(decimal, DateTime)>();
        for (var i = 0; i < amount.Count && i < dueDate.Count; i++)
            if (amount[i] > 0) rows.Add((amount[i], dueDate[i]));

        var result = await _installments.CreateAsync(invoice, rows, note, Actor);
        TempData[result.Success ? "Success" : "Error"] = result.Message;
        if (!result.Success) return RedirectToAction(nameof(Create), new { invoiceId });

        await _audit.LogAsync(Actor, "Create", "InstallmentPlan", result.Plan!.InstallmentPlanId, null,
            $"Installments: {rows.Count}; Total: {result.Plan.TotalAmount:N2}");
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Late()
    {
        if (!User.HasPermission(AdminPermissions.ManageInstallments)) return Forbid();

        var late = await _installments.LateInstallmentsAsync(User, DateTime.Today);
        var planIds = late.Select(i => i.InstallmentPlanId).Distinct().ToList();
        var plans = await _db.InstallmentPlans
            .Include(p => p.Installments)
            .Include(p => p.Invoice).ThenInclude(i => i!.Registration).ThenInclude(r => r!.Course)
            .Where(p => planIds.Contains(p.InstallmentPlanId))
            .ToListAsync();

        var today = DateTime.Today;
        var invoiceIds = plans.Select(p => p.InvoiceId).ToList();
        var paidByInvoice = await _db.Payments
            .Where(p => !p.IsCancelled && invoiceIds.Contains(p.InvoiceId))
            .GroupBy(p => p.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Total = g.Sum(p => p.Amount) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Total);
        var refundByInvoice = await _db.Refunds
            .Where(r => r.Status == RefundStatus.Approved && invoiceIds.Contains(r.InvoiceId))
            .GroupBy(r => r.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Total = g.Sum(r => r.Amount) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Total);

        var rows = plans.Select(plan =>
        {
            var netPaid = paidByInvoice.GetValueOrDefault(plan.InvoiceId) - refundByInvoice.GetValueOrDefault(plan.InvoiceId);
            var views = InstallmentService.Allocate(plan.Installments, netPaid, today);
            return new InstallmentService.LatePlanRow(plan, views.Where(v => v.Status == "Late").ToList());
        }).Where(r => r.LateViews.Count > 0).ToList();

        ViewBag.Rows = rows;
        return View();
    }

    /// <summary>Clean printable installment schedule (جدول الأقساط) for the trainee.</summary>
    public async Task<IActionResult> Print(int invoiceId)
    {
        if (!User.HasPermission(AdminPermissions.ManageInstallments)) return Forbid();

        var plan = await _db.InstallmentPlans
            .Include(p => p.Installments)
            .Include(p => p.Invoice).ThenInclude(i => i!.Registration).ThenInclude(r => r!.Course)
            .FirstOrDefaultAsync(p => p.InvoiceId == invoiceId);
        if (plan == null) return NotFound();
        if (!User.CanAccessCourse(plan.Invoice?.Registration?.CourseId)) return Forbid();

        var paid = await _db.Payments
            .Where(p => p.InvoiceId == invoiceId && !p.IsCancelled)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var refunded = await _db.Refunds
            .Where(r => r.InvoiceId == invoiceId && r.Status == RefundStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;

        ViewBag.Views = InstallmentService.Allocate(plan.Installments, paid - refunded, DateTime.Today);
        ViewBag.NetPaid = paid - refunded;
        ViewBag.InstitutionName = "مركز التدريب";
        return View(plan);
    }
}
