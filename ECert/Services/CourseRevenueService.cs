using System.Security.Claims;
using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services.Excel;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Builds the detailed course-revenue report (تقرير إيرادات الدورات) and its per-course drill-down.
/// Revenue is always derived from the ledgers — gross collections minus approved refunds — and every
/// query is confined to the caller's course scope. There is no "transfer to the fund" here: money
/// reaches the main fund only through an approved custody handover (<c>/Custody</c>).
/// </summary>
public class CourseRevenueService
{
    private readonly ECertDbContext _db;
    public CourseRevenueService(ECertDbContext db) => _db = db;

    /// <summary>The filters the report honours; all are optional.</summary>
    public sealed record Filters(int? CategoryId, int? CourseId, string? CourseStatus, DateTime? From, DateTime? To);

    /// <summary>One row per course plus the filter-aware KPI totals.</summary>
    public sealed record Report(List<CourseRevenueRowViewModel> Rows, CourseRevenueSummaryViewModel Summary);

    public async Task<Report> GetReportAsync(ClaimsPrincipal user, Filters filters)
    {
        var courses = await FilteredCoursesAsync(user, filters);
        var from = filters.From?.Date;
        var toExclusive = filters.To?.Date.AddDays(1);

        var refundsByCourse = await RefundsByCourseAsync(courses, from, toExclusive);
        var rows = courses.Select(c => BuildRow(c, refundsByCourse.GetValueOrDefault(c.CourseId), from, toExclusive)).ToList();

        var summary = new CourseRevenueSummaryViewModel
        {
            Expected = rows.Sum(r => r.Expected),
            NetPaid = rows.Sum(r => r.NetPaid),
            Cash = rows.Sum(r => r.Cash),
            Wallet = rows.Sum(r => r.Wallet),
            Remaining = rows.Sum(r => r.Remaining)
        };
        return new Report(rows, summary);
    }

    /// <summary>The per-course drill-down: trainee collections plus breakdowns by employee and method.</summary>
    public async Task<CourseRevenueDetailViewModel?> GetDetailAsync(ClaimsPrincipal user, int courseId, DateTime? from, DateTime? to)
    {
        var course = await _db.Courses.InScope(user)
            .Include(c => c.Category)
            .Include(c => c.Registrations).ThenInclude(r => r.Invoice).ThenInclude(i => i!.Payments)
            .FirstOrDefaultAsync(c => c.CourseId == courseId);
        if (course == null) return null;

        var fromDate = from?.Date;
        var toExclusive = to?.Date.AddDays(1);

        var invoices = course.Registrations.Where(r => r.Invoice != null).Select(r => r.Invoice!).ToList();
        var invoiceIds = invoices.Select(i => i.InvoiceId).ToList();
        var refunds = await _db.Refunds
            .Where(r => invoiceIds.Contains(r.InvoiceId) && r.Status == RefundStatus.Approved)
            .ToListAsync();

        var detail = new CourseRevenueDetailViewModel
        {
            CourseId = course.CourseId,
            CourseName = course.CourseName,
            CategoryName = course.Category?.CategoryName ?? string.Empty,
            Status = course.Status,
            From = fromDate,
            To = to?.Date,
            Expected = invoices.Sum(i => i.TotalAmount)
        };

        foreach (var invoice in invoices)
        {
            var payments = invoice.Payments.Where(p => !p.IsCancelled).ToList();
            var paid = payments.Where(p => InRange(p.PaymentDate, fromDate, toExclusive)).Sum(p => p.Amount);
            var refunded = refunds.Where(r => r.InvoiceId == invoice.InvoiceId && InRange(r.RefundDate, fromDate, toExclusive))
                .Sum(r => r.Amount);

            detail.Trainees.Add(new CourseRevenueTraineeRow
            {
                InvoiceId = invoice.InvoiceId,
                InvoiceNumber = invoice.InvoiceNumber,
                TraineeName = invoice.TraineeNameArabic ?? invoice.TraineeName,
                Phone = invoice.TraineePhone,
                Expected = invoice.TotalAmount,
                Paid = paid,
                Refunds = refunded,
                Status = invoice.Status,
                DueDate = invoice.DueDate
            });
        }

        detail.Paid = detail.Trainees.Sum(t => t.Paid);
        detail.Refunds = detail.Trainees.Sum(t => t.Refunds);
        detail.Trainees = detail.Trainees.OrderByDescending(t => t.Remaining).ThenBy(t => t.TraineeName).ToList();

        var paymentsInRange = invoices
            .SelectMany(i => i.Payments.Where(p => !p.IsCancelled && InRange(p.PaymentDate, fromDate, toExclusive)))
            .ToList();

        detail.ByEmployee = paymentsInRange
            .GroupBy(p => string.IsNullOrWhiteSpace(p.RecordedBy) ? "—" : p.RecordedBy!)
            .Select(g => new CourseRevenueBreakdownRow { Label = g.Key, Amount = g.Sum(p => p.Amount), Count = g.Count() })
            .OrderByDescending(r => r.Amount).ToList();

        detail.ByMethod = paymentsInRange
            .GroupBy(p => p.PaymentMethod)
            .Select(g => new CourseRevenueBreakdownRow
            {
                Label = ExcelLabels.PaymentMethod(g.Key),
                Amount = g.Sum(p => p.Amount),
                Count = g.Count()
            })
            .OrderByDescending(r => r.Amount).ToList();

        return detail;
    }

    /// <summary>Rows for the spreadsheet export, honouring the same filters as the page.</summary>
    public async Task<(List<CourseRevenueRowViewModel> Rows, CourseRevenueSummaryViewModel Summary)> GetExportAsync(ClaimsPrincipal user, Filters filters)
    {
        var report = await GetReportAsync(user, filters);
        return (report.Rows, report.Summary);
    }

    private async Task<List<Course>> FilteredCoursesAsync(ClaimsPrincipal user, Filters filters)
    {
        var query = _db.Courses.InScope(user)
            .Include(c => c.Category)
            .Include(c => c.Registrations).ThenInclude(r => r.Invoice).ThenInclude(i => i!.Payments)
            .AsQueryable();

        if (filters.CategoryId.HasValue)
            query = query.Where(c => c.CategoryId == filters.CategoryId.Value);
        if (filters.CourseId.HasValue)
            query = query.Where(c => c.CourseId == filters.CourseId.Value);
        if (!string.IsNullOrWhiteSpace(filters.CourseStatus))
            query = query.Where(c => c.Status == filters.CourseStatus);

        return await query.OrderBy(c => c.CourseName).ToListAsync();
    }

    /// <summary>
    /// Approved refunds per course, one grouped query instead of one per row (a report over every
    /// course would otherwise issue a query per course).
    /// </summary>
    private async Task<Dictionary<int, decimal>> RefundsByCourseAsync(List<Course> courses, DateTime? from, DateTime? toExclusive)
    {
        var courseIds = courses.Select(c => c.CourseId).ToList();
        var rows = await _db.Refunds
            .Where(r => r.Status == RefundStatus.Approved
                && r.Invoice != null && r.Invoice.Registration != null
                && courseIds.Contains(r.Invoice.Registration.CourseId)
                && (from == null || r.RefundDate >= from)
                && (toExclusive == null || r.RefundDate < toExclusive))
            .GroupBy(r => r.Invoice!.Registration!.CourseId)
            .Select(g => new { CourseId = g.Key, Total = g.Sum(r => r.Amount) })
            .ToListAsync();
        return rows.ToDictionary(r => r.CourseId, r => r.Total);
    }

    private static CourseRevenueRowViewModel BuildRow(Course course, decimal refunds, DateTime? from, DateTime? toExclusive)
    {
        var invoices = course.Registrations.Where(r => r.Invoice != null).Select(r => r.Invoice!).ToList();
        var payments = invoices.SelectMany(i => i.Payments)
            .Where(p => !p.IsCancelled && InRange(p.PaymentDate, from, toExclusive)).ToList();

        return new CourseRevenueRowViewModel
        {
            CourseId = course.CourseId,
            CourseName = course.CourseName,
            CategoryName = course.Category?.CategoryName ?? string.Empty,
            Status = course.Status,
            Registrations = course.Registrations.Count(r => r.Status == "Accepted"),
            Original = invoices.Sum(i => i.OriginalAmount),
            Exemptions = invoices.Sum(i => i.ExemptionAmount),
            Expected = invoices.Sum(i => i.TotalAmount),
            Paid = payments.Sum(p => p.Amount),
            Refunds = refunds,
            FullPaid = invoices.Count(i => i.Status == "Paid"),
            Partial = invoices.Count(i => i.Status == "PartiallyPaid"),
            Unpaid = invoices.Count(i => i.Status == "Unpaid"),
            Cash = payments.Where(p => p.PaymentMethod == "Cash").Sum(p => p.Amount),
            Wallet = payments.Where(p => p.PaymentMethod != "Cash").Sum(p => p.Amount)
        };
    }

    private static bool InRange(DateTime value, DateTime? from, DateTime? toExclusive)
    {
        if (from.HasValue && value < from.Value) return false;
        if (toExclusive.HasValue && value >= toExclusive.Value) return false;
        return true;
    }
}
