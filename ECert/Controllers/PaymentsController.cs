using System.Security.Claims;
using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services;
using ECert.Services.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ECert.Controllers;

[Authorize]
public class PaymentsController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    public PaymentsController(ECertDbContext db, AuditLogService audit) { _db = db; _audit = audit; }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    private static bool CanAccess(ClaimsPrincipal user, Invoice invoice) =>
        invoice.Registration != null && user.CanAccessCourse(invoice.Registration.CourseId);

    public async Task<IActionResult> Index(string? search, DateTime? dateFrom, DateTime? dateTo, string? method, string? status, string? courseName, string? studentName, string? invoiceNumber)
    {
        if (!HasPermission("manage-payments")) return Forbid();
        var query = _db.Payments.Include(p => p.Invoice).ThenInclude(i => i!.Registration).InScope(User).AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(p => p.Invoice!.InvoiceNumber.Contains(search) || p.Invoice.TraineeName.Contains(search) || (p.Invoice.TraineeNameArabic != null && p.Invoice.TraineeNameArabic.Contains(search)) || (p.Invoice.TraineeNameEnglish != null && p.Invoice.TraineeNameEnglish.Contains(search)) || p.RecordedBy!.Contains(search));
        if (!string.IsNullOrEmpty(studentName))
            query = query.Where(p => p.Invoice!.TraineeName.Contains(studentName) || (p.Invoice.TraineeNameArabic != null && p.Invoice.TraineeNameArabic.Contains(studentName)) || (p.Invoice.TraineeNameEnglish != null && p.Invoice.TraineeNameEnglish.Contains(studentName)));
        if (!string.IsNullOrEmpty(invoiceNumber))
            query = query.Where(p => p.Invoice!.InvoiceNumber.Contains(invoiceNumber));
        if (!string.IsNullOrEmpty(courseName))
            query = query.Where(p => p.Invoice!.CourseName.Contains(courseName));
        if (dateFrom.HasValue) query = query.Where(p => p.PaymentDate >= dateFrom.Value);
        if (dateTo.HasValue) query = query.Where(p => p.PaymentDate <= dateTo.Value.AddDays(1));
        if (!string.IsNullOrEmpty(method)) query = query.Where(p => p.PaymentMethod == method);
        if (!string.IsNullOrEmpty(status))
        {
            if (status == "Active") query = query.Where(p => !p.IsCancelled);
            else if (status == "Cancelled") query = query.Where(p => p.IsCancelled);
        }
        ViewBag.Payments = await query.OrderByDescending(p => p.PaymentDate).ToListAsync();
        ViewBag.Search = search;
        ViewBag.DateFrom = dateFrom;
        ViewBag.DateTo = dateTo;
        ViewBag.Method = method;
        ViewBag.Status = status;
        ViewBag.CourseName = courseName;
        ViewBag.StudentName = studentName;
        ViewBag.InvoiceNumber = invoiceNumber;
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Add(int invoiceId)
    {
        if (!HasPermission("manage-payments")) return Forbid();
        var invoice = await _db.Invoices.Include(i => i.Registration).FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);
        if (invoice == null) return NotFound();
        if (!CanAccess(User, invoice)) return Forbid();
        if (invoice.Status == "Cancelled")
        {
            TempData["Error"] = "لا يمكن تسجيل دفعة على فاتورة ملغية.";
            return RedirectToAction("Details", "Invoices", new { id = invoiceId });
        }
        ViewBag.Invoice = invoice;
        return View(new AddPaymentViewModel { InvoiceId = invoiceId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Add(AddPaymentViewModel model)
    {
        if (!HasPermission("manage-payments")) return Forbid();
        var invoice = await _db.Invoices.Include(i => i.Registration).FirstOrDefaultAsync(i => i.InvoiceId == model.InvoiceId);
        if (invoice == null) return NotFound();
        if (!CanAccess(User, invoice)) return Forbid();

        if (invoice.Status == "Cancelled")
        {
            TempData["Error"] = "لا يمكن تسجيل دفعة على فاتورة ملغية.";
            return RedirectToAction("Details", "Invoices", new { id = model.InvoiceId });
        }

        if (model.Amount <= 0)
        {
            TempData["Error"] = "المبلغ يجب أن يكون أكبر من صفر!";
            return RedirectToAction("Add", new { invoiceId = model.InvoiceId });
        }

        // Validate reference number for non-cash payments
        if (model.PaymentMethod != "Cash" && string.IsNullOrWhiteSpace(model.ReferenceNumber))
        {
            TempData["Error"] = "رقم السند مطلوب عند اختيار طريقة دفع إلكترونية!";
            return RedirectToAction("Add", new { invoiceId = model.InvoiceId });
        }

        if (model.Amount > invoice.RemainingAmount)
        {
            TempData["Error"] = "المبلغ أكبر من المتبقي!";
            return RedirectToAction("Add", new { invoiceId = model.InvoiceId });
        }

        var payment = new Payment
        {
            InvoiceId = model.InvoiceId,
            Amount = model.Amount,
            PaymentMethod = model.PaymentMethod,
            ReferenceNumber = model.PaymentMethod != "Cash" ? model.ReferenceNumber?.Trim() : null,
            PaymentDate = DateTime.Now,
            Notes = model.Notes,
            RecordedBy = User.Identity?.Name ?? "System",
            CreatedAt = DateTime.Now
        };

        // Two operators can submit at once; without serialising on the invoice row both would
        // pass the remaining-amount check and overpay. The lock plus a re-check closes that race.
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LockInvoiceRowAsync(invoice.InvoiceId);
        await _db.Entry(invoice).ReloadAsync();

        if (model.Amount > invoice.RemainingAmount)
        {
            await transaction.RollbackAsync();
            TempData["Error"] = "المبلغ أكبر من المتبقي!";
            return RedirectToAction("Add", new { invoiceId = model.InvoiceId });
        }

        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();

        await RecomputeInvoiceAsync(invoice);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "Payment", payment.PaymentId, null, $"Amount: {model.Amount}, Method: {model.PaymentMethod}");

        TempData["Success"] = $"تم تسجيل دفعة بقيمة {model.Amount} ريال بنجاح.";
        return RedirectToAction("Details", "Invoices", new { id = model.InvoiceId });
    }

    /// <summary>Takes an exclusive row lock so concurrent payments on one invoice serialise.</summary>
    private async Task LockInvoiceRowAsync(int invoiceId)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT `InvoiceId` FROM `Invoices` WHERE `InvoiceId` = @id FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = invoiceId;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id, string? reason)
    {
        if (!HasPermission("manage-payments")) return Forbid();
        var payment = await _db.Payments.Include(p => p.Invoice).ThenInclude(i => i!.Registration).FirstOrDefaultAsync(p => p.PaymentId == id);
        if (payment == null) return NotFound();
        if (payment.Invoice == null || !CanAccess(User, payment.Invoice)) return Forbid();

        payment.IsCancelled = true;
        payment.CancellationReason = reason;

        await _db.SaveChangesAsync();
        await RecomputeInvoiceAsync(payment.Invoice);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Cancel", "Payment", id, null, $"Reason: {reason}");

        TempData["Success"] = "تم إلغاء الدفعة بنجاح.";
        return RedirectToAction("Details", "Invoices", new { id = payment.InvoiceId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int id)
    {
        if (!HasPermission("manage-payments")) return Forbid();
        var payment = await _db.Payments.Include(p => p.Invoice).ThenInclude(i => i!.Registration).FirstOrDefaultAsync(p => p.PaymentId == id);
        if (payment == null) return NotFound();
        if (payment.Invoice == null || !CanAccess(User, payment.Invoice)) return Forbid();

        var invoiceId = payment.InvoiceId;
        var invoice = payment.Invoice;
        var amount = payment.Amount;

        _db.Payments.Remove(payment);
        await _db.SaveChangesAsync();
        await RecomputeInvoiceAsync(invoice);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Delete", "Payment", id, null, $"Deleted payment: {amount} ريال");

        TempData["Success"] = "تم حذف الدفعة بنجاح.";
        return RedirectToAction("Details", "Invoices", new { id = invoiceId });
    }

    public async Task<IActionResult> ExportXlsx(string? search, DateTime? dateFrom, DateTime? dateTo, string? method)
    {
        if (!HasPermission("manage-payments")) return Forbid();
        var query = _db.Payments.Include(p => p.Invoice).ThenInclude(i => i!.Registration).InScope(User).AsQueryable();
        if (!string.IsNullOrEmpty(search))
            query = query.Where(p => p.Invoice!.InvoiceNumber.Contains(search) || p.Invoice.TraineeName.Contains(search) || p.PaymentMethod.Contains(search) || p.RecordedBy!.Contains(search));
        if (dateFrom.HasValue) query = query.Where(p => p.PaymentDate >= dateFrom.Value);
        if (dateTo.HasValue) query = query.Where(p => p.PaymentDate <= dateTo.Value.AddDays(1));
        if (!string.IsNullOrEmpty(method)) query = query.Where(p => p.PaymentMethod == method);
        var list = await query.OrderByDescending(p => p.PaymentDate).ToListAsync();

        using var export = new ExcelExport("الدفعات",
            "رقم الفاتورة", "اسم الطالب بالعربية", "اسم الطالب بالإنجليزية", "المبلغ",
            "طريقة الدفع", "التاريخ", "الموظف", "ملاحظات");

        foreach (var p in list)
        {
            export.WriteRow(
                p.Invoice?.InvoiceNumber,
                p.Invoice?.TraineeNameArabic ?? p.Invoice?.TraineeName,
                p.Invoice?.TraineeNameEnglish ?? p.Invoice?.TraineeName,
                p.Amount,
                ExcelLabels.PaymentMethod(p.PaymentMethod),
                p.PaymentDate.ToString("yyyy-MM-dd"),
                p.RecordedBy,
                p.Notes);
        }

        return export.Build("payments");
    }

    /// <summary>
    /// Recomputes <c>PaidAmount</c> and the status from the invoice's active payments. Deriving the
    /// balance from the ledger (rather than adding/subtracting the delta) keeps it correct even if
    /// an earlier write failed or a payment was cancelled twice.
    /// </summary>
    private async Task RecomputeInvoiceAsync(Invoice invoice)
    {
        var paid = await _db.Payments
            .Where(p => p.InvoiceId == invoice.InvoiceId && !p.IsCancelled)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;

        invoice.PaidAmount = paid;
        invoice.Status = invoice.Status == "Cancelled"
            ? "Cancelled"
            : paid >= invoice.TotalAmount && invoice.TotalAmount > 0 ? "Paid"
            : paid > 0 ? "PartiallyPaid"
            : "Unpaid";
    }
}
