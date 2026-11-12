using System.Security.Claims;
using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Fee refunds (استرداد الرسوم). A refund is never a deletion of an old payment — it is a new dated
/// movement that reduces the invoice balance and debits the performing employee's custody, so the
/// original collection and its reversal both remain visible for reconciliation and audit.
/// </summary>
[Authorize]
public class RefundsController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly FinancialService _financial;
    private readonly FinancialLedgerService _ledger;
    private readonly ReceiptVerificationService _receipts;
    private readonly IConfiguration _configuration;
    private readonly SystemSettingsService _settings;

    public RefundsController(ECertDbContext db, AuditLogService audit, FinancialService financial,
        FinancialLedgerService ledger, ReceiptVerificationService receipts, IConfiguration configuration,
        SystemSettingsService settings)
    {
        _db = db;
        _audit = audit;
        _financial = financial;
        _ledger = ledger;
        _receipts = receipts;
        _configuration = configuration;
        _settings = settings;
    }

    private bool HasPermission(string perm) => User.HasPermission(perm);
    private int? CurrentUserId =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private string Actor => User.Identity?.Name ?? "System";

    private string InstitutionName =>
        _configuration["Institution:Name"] is { Length: > 0 } name ? name : "مركز التدريب";

    private string GetPublicBaseUrl()
    {
        var configured = _configuration["CertificateSecurity:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim().TrimEnd('/');
        return $"{Request.Scheme}://{Request.Host}";
    }

    /// <summary>Refunds inside the caller's course scope, newest first.</summary>
    public async Task<IActionResult> Index(string? search)
    {
        if (!HasPermission(AdminPermissions.ManageRefunds)) return Forbid();

        var query = _db.Refunds
            .Include(r => r.Invoice).ThenInclude(i => i!.Registration)
            .InScope(User)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(r => r.Invoice!.InvoiceNumber.Contains(search)
                || r.Invoice.TraineeName.Contains(search)
                || (r.ReceiptNumber != null && r.ReceiptNumber.Contains(search)));

        ViewBag.Refunds = await query.OrderByDescending(r => r.RefundDate).ThenByDescending(r => r.RefundId).Take(500).ToListAsync();
        ViewBag.Search = search;
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Create(int invoiceId)
    {
        if (!HasPermission(AdminPermissions.ManageRefunds)) return Forbid();
        var invoice = await _db.Invoices.Include(i => i.Registration).FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);
        if (invoice?.Registration == null) return NotFound();
        if (!User.CanAccessCourse(invoice.Registration.CourseId)) return Forbid();
        if (invoice.Status == "Cancelled")
        {
            TempData["Error"] = "لا يمكن استرداد رسوم من فاتورة ملغية.";
            return RedirectToAction("Details", "Invoices", new { id = invoiceId });
        }

        ViewBag.Invoice = invoice;
        ViewBag.RefundedAmount = await _financial.GetRefundedAmountAsync(invoiceId);
        ViewBag.IsSuperAdmin = User.IsSuperAdmin();
        ViewBag.CustodyBalance = CurrentUserId.HasValue
            ? await _ledger.GetCustodyBalanceAsync(CurrentUserId.Value)
            : 0m;
        ViewBag.FundBalance = await _ledger.GetMainFundBalanceAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int invoiceId, decimal amount, string reason, string refundMethod, string? receiptNumber)
    {
        if (!HasPermission(AdminPermissions.ManageRefunds)) return Forbid();
        var invoice = await _db.Invoices.Include(i => i.Registration).FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);
        if (invoice?.Registration == null) return NotFound();
        if (!User.CanAccessCourse(invoice.Registration.CourseId)) return Forbid();

        if (!CurrentUserId.HasValue)
        {
            TempData["Error"] = "تعذّر تحديد المستخدم الحالي لتسجيل الاسترداد.";
            return RedirectToAction("Details", "Invoices", new { id = invoiceId });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var result = await _financial.CreateRefundAsync(invoice, amount, reason, refundMethod, receiptNumber,
            CurrentUserId.Value, Actor, isSuperAdmin: User.IsSuperAdmin());

        if (!result.Success)
        {
            await transaction.RollbackAsync();
            TempData["Error"] = result.Message;
            return RedirectToAction("Details", "Invoices", new { id = invoiceId });
        }

        await transaction.CommitAsync();
        await _audit.LogAsync(Actor, "Create", "Refund", result.Refund!.RefundId, null,
            $"Refund: {amount:N2}; Invoice: {invoice.InvoiceNumber}; Reason: {reason}");

        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Receipt), new { id = result.Refund!.RefundId });
    }

    /// <summary>
    /// Approves a refund that was waiting for approval, after re-checking the refundable ceiling so a
    /// crafted request cannot push an invoice past fully refunded. SuperAdmin only; the caller must
    /// already be inside a transaction.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id)
    {
        if (!HasPermission(AdminPermissions.ManageRefunds)) return Forbid();
        if (!User.IsSuperAdmin())
        {
            TempData["Error"] = "اعتماد الاستردادات يتطلب صلاحية مدير النظام.";
            return RedirectToAction(nameof(Index));
        }

        var refund = await _db.Refunds.Include(r => r.Invoice).ThenInclude(i => i!.Registration)
            .FirstOrDefaultAsync(r => r.RefundId == id);
        if (refund?.Invoice?.Registration == null) return NotFound();
        if (!User.CanAccessCourse(refund.Invoice.Registration.CourseId)) return Forbid();

        if (!CurrentUserId.HasValue)
        {
            TempData["Error"] = "تعذّر تحديد المستخدم الحالي.";
            return RedirectToAction(nameof(Index));
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        // Re-check the ceiling: the invoice may have changed since the request was created.
        await _financial.RecomputeInvoiceAsync(refund.Invoice);
        var available = refund.Invoice.PaidAmount;
        if (refund.Amount > available)
        {
            await transaction.RollbackAsync();
            TempData["Error"] = $"لا يمكن الاعتماد: المبلغ المتاح للاسترداد ({available:N2}) أقل من قيمة الطلب ({refund.Amount:N2}).";
            return RedirectToAction(nameof(Index));
        }

        var result = await _financial.ApproveRefundAsync(refund, CurrentUserId.Value, Actor);
        if (!result.Success)
        {
            await transaction.RollbackAsync();
            TempData["Error"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        await transaction.CommitAsync();
        await _audit.LogAsync(Actor, "Approve", "Refund", refund.RefundId, null,
            $"Approve refund: {refund.Amount:N2}; Invoice: {refund.Invoice.InvoiceNumber}");
        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Printable refund voucher (سند صرف) with a public verification QR and WhatsApp share link.</summary>
    [HttpGet]
    public async Task<IActionResult> Receipt(int id)
    {
        if (!HasPermission(AdminPermissions.ManageRefunds)) return Forbid();
        var refund = await _db.Refunds
            .Include(r => r.Invoice).ThenInclude(i => i!.Registration)
            .FirstOrDefaultAsync(r => r.RefundId == id);
        if (refund?.Invoice?.Registration == null) return NotFound();
        if (!User.CanAccessCourse(refund.Invoice.Registration.CourseId)) return Forbid();

        if (string.IsNullOrWhiteSpace(refund.PublicId))
        {
            await _ledger.EnsureRefundPublicIdAsync(refund);
            await _db.SaveChangesAsync();
        }

        ViewBag.VerificationUrl = _receipts.BuildVerificationUrl(refund.PublicId!, GetPublicBaseUrl());
        ViewBag.QrDataUri = _receipts.BuildQrDataUri((string)ViewBag.VerificationUrl);
        ViewBag.WhatsAppLink = WhatsAppLinkBuilder.BuildLink(refund.Invoice.TraineePhone,
            WhatsAppLinkBuilder.BuildReceiptMessage(
                InstitutionName, "سند استرداد", refund.ReceiptNumber ?? $"R-{refund.RefundId}",
                refund.Invoice.TraineeName, refund.Invoice.CourseName, refund.Amount,
                refund.Invoice.RemainingAmount, (string)ViewBag.VerificationUrl,
                await _settings.GetCurrencySymbolAsync()));
        return View(refund);
    }
}
