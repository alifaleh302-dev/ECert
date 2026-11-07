using ECert.Data;
using ECert.Models;
using ECert.Models.ApiModels;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers.Api;

[ApiController]
[Route("api/invoices")]
[Authorize]
public class InvoicesApiController : ControllerBase
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly RegistrationInvoiceService _invoiceService;
    public InvoicesApiController(ECertDbContext db, AuditLogService audit, RegistrationInvoiceService invoiceService) { _db = db; _audit = audit; _invoiceService = invoiceService; }

    private bool HasPermission(string perm) => User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    [HttpGet]
    public async Task<IActionResult> Get(string? status)
    {
        if (!User.CanAccessSection("Invoices"))
            return Ok(ApiResponse.Fail("ليس لديك صلاحية الوصول"));

        var query = _db.Invoices.AsQueryable();
        if (!string.IsNullOrEmpty(status)) query = query.Where(i => i.Status == status);

        var invoices = await query.OrderByDescending(i => i.CreatedAt)
            .Select(i => new
            {
                i.InvoiceId, i.InvoiceNumber, i.TraineeName, i.TraineeNameArabic, i.TraineeNameEnglish, i.TraineePhone,
                i.CourseName, i.CourseNameArabic, i.CourseNameEnglish, i.TotalAmount, i.PaidAmount,
                remainingAmount = i.TotalAmount - i.PaidAmount,
                i.Status, i.CreatedAt, i.DueDate
            }).ToListAsync();
        return Ok(ApiResponse<object>.Ok(invoices));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(int id)
    {
        if (!HasPermission("view-invoices"))
            return Ok(ApiResponse.Fail("ليس لديك صلاحية الوصول"));

        var invoice = await _db.Invoices.Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.InvoiceId == id);
        if (invoice == null) return NotFound(ApiResponse.Fail("الفاتورة غير موجودة"));

        return Ok(ApiResponse<object>.Ok(new
        {
            invoice.InvoiceId, invoice.InvoiceNumber, invoice.TraineeName, invoice.TraineeNameArabic, invoice.TraineeNameEnglish,
            invoice.TraineePhone, invoice.CourseName, invoice.CourseNameArabic, invoice.CourseNameEnglish, invoice.TotalAmount,
            invoice.PaidAmount, remainingAmount = invoice.TotalAmount - invoice.PaidAmount,
            invoice.Status, invoice.CreatedAt, invoice.DueDate, invoice.CreatedBy,
            payments = invoice.Payments.Select(p => new
            {
                p.PaymentId, p.Amount, p.PaymentMethod, p.PaymentDate, p.Notes, p.RecordedBy, p.IsCancelled
            })
        }));
    }

    public record CreateInvoiceRequest(int RegistrationId, DateTime? DueDate);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateInvoiceRequest req)
    {
        if (!HasPermission("manage-finance"))
            return Ok(ApiResponse.Fail("ليس لديك صلاحية الوصول"));

        var reg = await _db.Registrations.Include(r => r.Course).Include(r => r.Invoice)
            .FirstOrDefaultAsync(r => r.RegistrationId == req.RegistrationId);
        if (reg == null) return Ok(ApiResponse.Fail("التسجيل غير موجود"));
        if (reg.Status != "Accepted") return Ok(ApiResponse.Fail("لا يمكن إنشاء فاتورة إلا لتسجيل مقبول"));

        var invoice = await _invoiceService.EnsureForAcceptedAsync(reg, User.Identity?.Name ?? "System");
        invoice.DueDate = req.DueDate;
        await _db.SaveChangesAsync();
        await _audit.LogAsync(User.Identity?.Name ?? "", "Create", "Invoice", invoice.InvoiceId, null, invoice.InvoiceNumber);

        return Ok(ApiResponse<object>.Ok(new { id = invoice.InvoiceId, invoiceNumber = invoice.InvoiceNumber }, "تم إنشاء/تأكيد الفاتورة بنجاح"));
    }
}
