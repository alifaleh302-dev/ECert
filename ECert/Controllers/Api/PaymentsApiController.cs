using ECert.Data;
using ECert.Models;
using ECert.Models.ApiModels;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace ECert.Controllers.Api;

[ApiController]
[Route("api/payments")]
[Authorize]
public class PaymentsApiController : ControllerBase
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly FinancialLedgerService _ledger;
    private readonly SystemSettingsService _settings;
    private readonly PeriodLockService _periods;
    public PaymentsApiController(ECertDbContext db, AuditLogService audit, FinancialLedgerService ledger,
        SystemSettingsService settings, PeriodLockService periods)
    { _db = db; _audit = audit; _ledger = ledger; _settings = settings; _periods = periods; }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    private int? CurrentUserId =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

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

        if (await _periods.IsLockedAsync(DateTime.Now))
            return Ok(ApiResponse.Fail((await _periods.BlockedMessageAsync(DateTime.Now))!));

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

        // Mirror the web path: the collected amount lands in the collector's custody and is audited.
        if (CurrentUserId is { } collectorId)
        {
            await _ledger.EnsurePaymentPublicIdAsync(payment);
            _ledger.AddCollection(collectorId, payment.Amount, payment.RecordedBy,
                payment.PaymentId, payment.ReferenceNumber, $"قبض على الفاتورة {invoice.InvoiceNumber}");
        }

        await RecomputeInvoiceAsync(invoice);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "Payment", payment.PaymentId, null, $"Amount: {req.Amount}");

        return Ok(ApiResponse<object>.Ok(new { id = payment.PaymentId }, $"تم تسجيل دفعة بقيمة {req.Amount:N2} {await _settings.GetCurrencySymbolAsync()} بنجاح"));
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

        if (await _periods.IsLockedAsync(payment.PaymentDate))
            return Ok(ApiResponse.Fail((await _periods.BlockedMessageAsync(payment.PaymentDate))!));

        // A repeated cancel must not reverse the collector's custody twice.
        if (payment.IsCancelled)
            return Ok(ApiResponse.Ok("تم إلغاء الدفعة بنجاح"));

        payment.IsCancelled = true;
        payment.CancellationReason = req?.Reason;

        await _db.SaveChangesAsync();
        await RecomputeInvoiceAsync(payment.Invoice);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Cancel", "Payment", id, null, $"Reason: {req?.Reason}");

        return Ok(ApiResponse.Ok("تم إلغاء الدفعة بنجاح"));
    }

    /// <summary>Derives the invoice balance from its active payments minus its refunds so it can never drift.</summary>
    private async Task RecomputeInvoiceAsync(Invoice invoice)
    {
        var paid = await _db.Payments
            .Where(p => p.InvoiceId == invoice.InvoiceId && !p.IsCancelled)
            .SumAsync(p => (decimal?)p.Amount) ?? 0m;
        var refunded = await _db.Refunds
            .Where(r => r.InvoiceId == invoice.InvoiceId && r.Status == RefundStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount) ?? 0m;
        var net = paid - refunded;

        invoice.PaidAmount = net;
        invoice.Status = invoice.Status == "Cancelled"
            ? "Cancelled"
            : net >= invoice.TotalAmount && invoice.TotalAmount > 0 ? "Paid"
            : net > 0 ? "PartiallyPaid"
            : "Unpaid";
    }
}
