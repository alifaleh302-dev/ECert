using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services;
using ECert.Services.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Receivables / outstanding fees (المتأخرات) and per-trainee statements (كشف حساب). Everything is
/// confined to the caller's course scope, so a course-scoped collector only ever sees the balances
/// of their own courses.
/// </summary>
[Authorize]
public class ReceivablesController : Controller
{
    private readonly ECertDbContext _db;
    public ReceivablesController(ECertDbContext db) => _db = db;

    public async Task<IActionResult> Index(int? courseId, string? search, string? bucket)
    {
        if (!User.HasPermission(AdminPermissions.ViewReceivables)) return Forbid();

        ViewBag.Courses = await _db.Courses.InScope(User).OrderBy(c => c.CourseName).ToListAsync();
        ViewBag.CourseId = courseId;
        ViewBag.Search = search;
        ViewBag.Bucket = bucket;

        var rows = await BuildRowsAsync(courseId, search);
        if (!string.IsNullOrEmpty(bucket))
            rows = rows.Where(r => r.Bucket == bucket).ToList();

        ViewBag.Data = rows;
        ViewBag.TotalOutstanding = rows.Sum(r => r.Remaining);
        ViewBag.Count = rows.Count;
        return View();
    }

    public async Task<IActionResult> ExportXlsx(int? courseId, string? search)
    {
        if (!User.HasPermission(AdminPermissions.ViewReceivables)) return Forbid();

        var rows = await BuildRowsAsync(courseId, search);

        using var export = new ExcelExport("المتأخرات",
            "رقم الفاتورة", "المتدرب", "الهاتف", "الدورة", "المستحق", "المدفوع", "المتبقي",
            "تاريخ الاستحقاق", "أيام التأخر", "الفئة");

        foreach (var r in rows)
        {
            export.WriteRow(
                r.InvoiceNumber, r.TraineeName, r.TraineePhone, r.CourseName,
                r.Total, r.Paid, r.Remaining,
                r.DueDate?.ToString("yyyy-MM-dd"), r.DaysOverdue, r.BucketLabel);
        }

        return export.Build("receivables");
    }

    /// <summary>Per-trainee statement (كشف حساب): the invoice plus every payment and refund.</summary>
    public async Task<IActionResult> Statement(int invoiceId)
    {
        if (!User.HasPermission(AdminPermissions.ViewReceivables)) return Forbid();

        var invoice = await _db.Invoices
            .Include(i => i.Registration).ThenInclude(r => r!.Course)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.InvoiceId == invoiceId);
        if (invoice?.Registration == null) return NotFound();
        if (!User.CanAccessCourse(invoice.Registration.CourseId)) return Forbid();

        var refunds = await _db.Refunds.Where(r => r.InvoiceId == invoiceId).OrderBy(r => r.RefundDate).ToListAsync();

        ViewBag.Invoice = invoice;
        ViewBag.Refunds = refunds;
        ViewBag.Refunded = refunds.Sum(r => r.Amount);
        return View();
    }

    private async Task<List<ReceivableRow>> BuildRowsAsync(int? courseId, string? search)
    {
        var query = _db.Invoices
            .Include(i => i.Registration).ThenInclude(r => r!.Course)
            .Include(i => i.Payments)
            .InScope(User)
            .Where(i => i.Status != "Cancelled" && i.Status != "Paid" && i.Registration != null);

        if (courseId.HasValue)
            query = query.Where(i => i.Registration!.CourseId == courseId.Value);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(i => i.InvoiceNumber.Contains(search)
                || i.TraineeName.Contains(search)
                || i.TraineePhone.Contains(search));

        var invoices = await query.OrderBy(i => i.DueDate).ThenBy(i => i.CreatedAt).ToListAsync();
        var refundsByInvoice = await _db.Refunds
            .Where(r => invoices.Select(i => i.InvoiceId).Contains(r.InvoiceId))
            .GroupBy(r => r.InvoiceId)
            .Select(g => new { InvoiceId = g.Key, Total = g.Sum(r => r.Amount) })
            .ToDictionaryAsync(x => x.InvoiceId, x => x.Total);

        var today = DateTime.Today;
        var rows = new List<ReceivableRow>();
        foreach (var invoice in invoices)
        {
            var paid = invoice.Payments.Where(p => !p.IsCancelled).Sum(p => p.Amount)
                - (refundsByInvoice.TryGetValue(invoice.InvoiceId, out var rf) ? rf : 0m);
            var remaining = invoice.TotalAmount - paid;
            if (remaining <= 0) continue;

            var daysOverdue = invoice.DueDate.HasValue ? (today - invoice.DueDate.Value.Date).Days : 0;
            var (bucket, label) = Classify(daysOverdue, invoice.DueDate);

            rows.Add(new ReceivableRow
            {
                InvoiceId = invoice.InvoiceId,
                InvoiceNumber = invoice.InvoiceNumber,
                TraineeName = invoice.TraineeNameArabic ?? invoice.TraineeName,
                TraineePhone = invoice.TraineePhone,
                CourseName = invoice.CourseNameArabic ?? invoice.CourseName,
                Total = invoice.TotalAmount,
                Paid = paid,
                Remaining = remaining,
                DueDate = invoice.DueDate,
                DaysOverdue = daysOverdue,
                Bucket = bucket,
                BucketLabel = label
            });
        }

        return rows.OrderByDescending(r => r.Remaining).ToList();
    }

    private static (string Bucket, string Label) Classify(int daysOverdue, DateTime? dueDate)
    {
        if (!dueDate.HasValue) return ("none", "بلا تاريخ استحقاق");
        if (daysOverdue <= 0) return ("current", "غير مستحق بعد");
        if (daysOverdue <= 30) return ("d30", "متأخر 1–30 يوم");
        if (daysOverdue <= 60) return ("d60", "متأخر 31–60 يوم");
        if (daysOverdue <= 90) return ("d90", "متأخر 61–90 يوم");
        return ("d90plus", "متأخر أكثر من 90 يوم");
    }
}

public class ReceivableRow
{
    public int InvoiceId { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public string TraineeName { get; set; } = string.Empty;
    public string TraineePhone { get; set; } = string.Empty;
    public string CourseName { get; set; } = string.Empty;
    public decimal Total { get; set; }
    public decimal Paid { get; set; }
    public decimal Remaining { get; set; }
    public DateTime? DueDate { get; set; }
    public int DaysOverdue { get; set; }
    public string Bucket { get; set; } = string.Empty;
    public string BucketLabel { get; set; } = string.Empty;
}
