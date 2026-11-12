using ECert.Data;
using ECert.Models;
using ECert.Models.ApiModels;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers.Api;

[ApiController]
[Route("api/payments")]
[Authorize]
public class PaymentsApiController : ControllerBase
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    public PaymentsApiController(ECertDbContext db, AuditLogService audit) { _db = db; _audit = audit; }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    private bool CanAccess(Invoice invoice) =>
        invoice.Registration != null && User.CanAccessCourse(invoice.Registration.CourseId);

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        if (!HasPermission("manage-payments"))
            return Ok(ApiResponse.Fail("ليس لديك صلاحية الوصول"));

        var payments = await _db.Payments.InScope(User).Include(p => p.Invoice)
            .OrderByDescending(p => p.PaymentDate)
            .Select(p => new
            {
                p.PaymentId, p.Amount, p.PaymentMethod, p.PaymentDate,
                p.Notes, p.RecordedBy, p.IsCancelled,
                invoiceNumber = p.Invoice!.InvoiceNumber,
                traineeName = p.Invoice.TraineeName
            }).ToListAsync();
        return Ok(ApiResponse<object>.Ok(payments));
    }

    public record AddPaymentRequest(int InvoiceId, decimal Amount, string PaymentMethod, string? Notes);

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddPaymentRequest req)
    {
        if (!HasPermission("manage-payments"))
            return Ok(ApiResponse.Fail("ليس لديك صلاحية الوصول"));

        var invoice = await _db.Invoices.Include(i => i.Registration).FirstOrDefaultAsync(i => i.InvoiceId == req.InvoiceId);
        if (invoice == null) return NotFound(ApiResponse.Fail("الفاتورة غير موجودة"));
        if (!CanAccess(invoice)) return Ok(ApiResponse.Fail("ليس لديك صلاحية الوصول"));
        if (invoice.Status == "Cancelled") return Ok(ApiResponse.Fail("لا يمكن تسجيل دفعة على فاتورة ملغية."));

        var remaining = invoice.TotalAmount - invoice.PaidAmount;
        if (req.Amount > remaining)
            return Ok(ApiResponse.Fail("المبلغ أكبر من المتبقي!"));

        if (req.Amount <= 0)
            return Ok(ApiResponse.Fail("المبلغ يجب أن يكون أكبر من صفر"));

        var payment = new Payment
        {
            InvoiceId = req.InvoiceId,
            Amount = req.Amount,
            PaymentMethod = req.PaymentMethod,
            PaymentDate = DateTime.Now,
            Notes = req.Notes,
            RecordedBy = User.Identity?.Name ?? "System",
            CreatedAt = DateTime.Now
        };

        _db.Payments.Add(payment);
        await _db.SaveChangesAsync();
        await RecomputeInvoiceAsync(invoice);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "Payment", payment.PaymentId, null, $"Amount: {req.Amount}");

        return Ok(ApiResponse<object>.Ok(new { id = payment.PaymentId }, $"تم تسجيل دفعة بقيمة {req.Amount} ريال بنجاح"));
    }

    public record CancelPaymentRequest(string? Reason);

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(int id, [FromBody] CancelPaymentRequest? req)
    {
        if (!HasPermission("manage-payments"))
            return Ok(ApiResponse.Fail("ليس لديك صلاحية الوصول"));

        var payment = await _db.Payments.Include(p => p.Invoice).ThenInclude(i => i!.Registration).FirstOrDefaultAsync(p => p.PaymentId == id);
        if (payment == null) return NotFound(ApiResponse.Fail("الدفعة غير موجودة"));
        if (payment.Invoice == null || !CanAccess(payment.Invoice)) return Ok(ApiResponse.Fail("ليس لديك صلاحية الوصول"));

        payment.IsCancelled = true;
        payment.CancellationReason = req?.Reason;

        await _db.SaveChangesAsync();
        await RecomputeInvoiceAsync(payment.Invoice);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Cancel", "Payment", id, null, $"Reason: {req?.Reason}");

        return Ok(ApiResponse.Ok("تم إلغاء الدفعة بنجاح"));
    }

    /// <summary>Derives the invoice balance from its active payments so it can never drift.</summary>
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
