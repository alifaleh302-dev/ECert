using ECert.Data;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Public, anonymous verification of a payment or refund receipt by its opaque public id and HMAC
/// signature (the same scheme used for certificate verification). It reveals only enough to confirm
/// a receipt is genuine — never the sequential key, never other receipts.
/// </summary>
[AllowAnonymous]
public class ReceiptVerificationController : Controller
{
    private readonly ECertDbContext _db;
    private readonly ReceiptVerificationService _receipts;

    public ReceiptVerificationController(ECertDbContext db, ReceiptVerificationService receipts)
    {
        _db = db;
        _receipts = receipts;
    }

    [HttpGet("/r/{publicId}")]
    public async Task<IActionResult> Verify(string publicId, string? sig)
    {
        var model = new PublicReceiptVerificationViewModel { PublicId = publicId?.Trim() ?? string.Empty };

        if (string.IsNullOrWhiteSpace(publicId) || !_receipts.VerifySignature(publicId, sig))
        {
            model.IsFound = false;
            model.Message = "رابط التحقق غير صالح أو منتهي.";
            return View(model);
        }

        var normalized = publicId.Trim();

        var payment = await _db.Payments.AsNoTracking()
            .Include(p => p.Invoice)
            .FirstOrDefaultAsync(p => p.PublicId == normalized);
        if (payment != null)
        {
            model.IsFound = true;
            model.Kind = "سند قبض";
            model.ReceiptNumber = payment.ReferenceNumber ?? $"P-{payment.PaymentId}";
            model.Amount = payment.Amount;
            model.TraineeName = payment.Invoice?.TraineeNameArabic ?? payment.Invoice?.TraineeName ?? "—";
            model.CourseName = payment.Invoice?.CourseNameArabic ?? payment.Invoice?.CourseName ?? "—";
            model.Date = payment.PaymentDate;
            model.RecordedBy = payment.RecordedBy;
            model.IsCancelled = payment.IsCancelled;
            model.Message = payment.IsCancelled
                ? "تم العثور على السند، لكن هذه الدفعة ملغاة."
                : "سند قبض صحيح ومسجَّل في النظام.";
            return View(model);
        }

        var refund = await _db.Refunds.AsNoTracking()
            .Include(r => r.Invoice)
            .FirstOrDefaultAsync(r => r.PublicId == normalized);
        if (refund != null)
        {
            model.IsFound = true;
            model.Kind = "سند استرداد";
            model.ReceiptNumber = refund.ReceiptNumber ?? $"R-{refund.RefundId}";
            model.Amount = refund.Amount;
            model.TraineeName = refund.Invoice?.TraineeNameArabic ?? refund.Invoice?.TraineeName ?? "—";
            model.CourseName = refund.Invoice?.CourseNameArabic ?? refund.Invoice?.CourseName ?? "—";
            model.Date = refund.RefundDate;
            model.RecordedBy = refund.RefundedBy;
            model.Message = "سند استرداد صحيح ومسجَّل في النظام.";
            return View(model);
        }

        model.IsFound = false;
        model.Message = "تعذّر العثور على سند مطابق. تأكد من صحة الرابط.";
        return View(model);
    }
}

public class PublicReceiptVerificationViewModel
{
    public string PublicId { get; set; } = string.Empty;
    public bool IsFound { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string ReceiptNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string TraineeName { get; set; } = string.Empty;
    public string CourseName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string RecordedBy { get; set; } = string.Empty;
    public bool IsCancelled { get; set; }
    public string Message { get; set; } = string.Empty;
}
