using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class InvoicesController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly RegistrationInvoiceService _invoiceService;
    public InvoicesController(ECertDbContext db, AuditLogService audit, RegistrationInvoiceService invoiceService) { _db = db; _audit = audit; _invoiceService = invoiceService; }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    /// <summary>An invoice is reachable only when its registration's course is inside the scope.</summary>
    private bool CanAccess(Invoice invoice) =>
        invoice.Registration != null && User.CanAccessCourse(invoice.Registration.CourseId);

    public async Task<IActionResult> Index(string? status, string? search)
    {
        // The section is opened by the read-only view-invoices permission or the finance umbrella.
        if (!User.CanAccessSection("Invoices")) return Forbid();
        var query = _db.Invoices.Include(i => i.Payments).Include(i => i.Registration).InScope(User).AsQueryable();
        if (!string.IsNullOrEmpty(status)) query = query.Where(i => i.Status == status);
        if (!string.IsNullOrEmpty(search))
        {
            query = query.Where(i => i.InvoiceNumber.Contains(search) || i.TraineeName.Contains(search) || (i.TraineeNameArabic != null && i.TraineeNameArabic.Contains(search)) || (i.TraineeNameEnglish != null && i.TraineeNameEnglish.Contains(search)) || i.TraineePhone.Contains(search) || i.CourseName.Contains(search) || (i.CourseNameArabic != null && i.CourseNameArabic.Contains(search)) || (i.CourseNameEnglish != null && i.CourseNameEnglish.Contains(search)));
        }
        ViewBag.Invoices = await query.OrderByDescending(i => i.CreatedAt).ToListAsync();
        ViewBag.Status = status;
        ViewBag.Search = search;
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Create(int registrationId)
    {
        if (!HasPermission("manage-finance")) return Forbid();
        var reg = await _db.Registrations.Include(r => r.Course).Include(r => r.Invoice).FirstOrDefaultAsync(r => r.RegistrationId == registrationId);
        if (reg == null) return NotFound();
        if (!User.CanAccessCourseOf(reg)) return Forbid();
        if (reg.Status != "Accepted") { TempData["Error"] = "لا يمكن إنشاء فاتورة إلا لتسجيل مقبول."; return RedirectToAction("Index"); }
        if (reg.Invoice != null) { TempData["Error"] = $"توجد فاتورة بالفعل لهذا التسجيل: {reg.Invoice.InvoiceNumber}"; return RedirectToAction("Index"); }

        ViewBag.Registration = reg;
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int registrationId, DateTime? dueDate)
    {
        if (!HasPermission("manage-finance")) return Forbid();
        var reg = await _db.Registrations.Include(r => r.Course).FirstOrDefaultAsync(r => r.RegistrationId == registrationId);
        if (reg == null) return NotFound();
        if (!User.CanAccessCourseOf(reg)) return Forbid();

        if (reg.Status != "Accepted")
        {
            TempData["Error"] = "لا يمكن إنشاء فاتورة إلا لتسجيل مقبول.";
            return RedirectToAction("Index");
        }

        var invoice = await _invoiceService.EnsureForAcceptedAsync(reg, User.Identity?.Name ?? "System");
        invoice.DueDate = dueDate;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "Invoice", invoice.InvoiceId, null, $"Invoice: {invoice.InvoiceNumber}, Amount: {invoice.TotalAmount}");

        TempData["Success"] = $"تم إنشاء/تأكيد الفاتورة {invoice.InvoiceNumber} بنجاح.";
        return RedirectToAction("Index");
    }

    public async Task<IActionResult> Details(int id)
    {
        // Same gate as the listing: the finance umbrella also opens an invoice's detail page.
        if (!User.CanAccessSection("Invoices")) return Forbid();
        var invoice = await _db.Invoices.Include(i => i.Payments).Include(i => i.Registration).ThenInclude(r => r!.Course).FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound();
        if (!CanAccess(invoice)) return Forbid();
        return View(invoice);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        if (!HasPermission("manage-finance")) return Forbid();
        var invoice = await _db.Invoices.Include(i => i.Registration).Include(i => i.Payments).FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound();
        if (!CanAccess(invoice)) return Forbid();

        // Cancelling an invoice that still carries money would silently strand those payments,
        // so the operator must first delete the payments (which recomputes the balance).
        if (invoice.Payments.Any(p => !p.IsCancelled))
        {
            TempData["Error"] = "لا يمكن إلغاء فاتورة تحتوي على دفعات نشطة. احذف الدفعات أولاً.";
            return RedirectToAction("Details", new { id });
        }

        invoice.Status = "Cancelled";
        invoice.CancelledBy = User.Identity?.Name ?? "System";
        invoice.CancelledAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Cancel", "Invoice", id);
        TempData["Success"] = "تم إلغاء الفاتورة.";
        return RedirectToAction("Index");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(int id)
    {
        if (!HasPermission("manage-finance")) return Forbid();
        var invoice = await _db.Invoices.Include(i => i.Registration).FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound();
        if (!CanAccess(invoice)) return Forbid();
        // Restore to appropriate status based on paid amount
        if (invoice.PaidAmount >= invoice.TotalAmount)
            invoice.Status = "Paid";
        else if (invoice.PaidAmount > 0)
            invoice.Status = "PartiallyPaid";
        else
            invoice.Status = "Unpaid";
        invoice.RestoredBy = User.Identity?.Name ?? "System";
        invoice.RestoredAt = DateTime.Now;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Restore", "Invoice", id);
        TempData["Success"] = "تم استعادة الفاتورة.";
        return RedirectToAction("Index");
    }
}
