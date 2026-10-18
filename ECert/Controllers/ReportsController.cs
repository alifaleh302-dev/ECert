using System.Data;
using ECert.Data;
using ECert.Models;
using ECert.Models.ViewModels;
using ECert.Services.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

[Authorize]
public class ReportsController : Controller
{
    private readonly ECertDbContext _db;
    public ReportsController(ECertDbContext db) => _db = db;

    private bool HasPermission(string perm) =>
        User.HasClaim(c => c.Type == "Permission" && c.Value == perm);

    private bool CanTransfer =>
        User.IsInRole(CashboxRoles.SystemManager)
        || User.HasClaim(c => c.Type == "Permission" && c.Value == CashboxRoles.PermTransfer);

    public IActionResult Index() => RedirectToAction("CourseRevenue");

    public async Task<IActionResult> CourseRevenue(int? courseId)
    {
        if (!HasPermission("view-reports")) return Forbid();

        // Razor iterates ViewBag values as dynamic; anonymous-type properties are
        // inaccessible to the generated view and cause RuntimeBinderException.
        ViewBag.Courses = await _db.Courses.OrderBy(c => c.CourseName).ToListAsync();
        ViewBag.CourseId = courseId;
        ViewBag.CanTransfer = CanTransfer;

        var query = _db.Courses
            .Include(c => c.Registrations).ThenInclude(r => r.Invoice).ThenInclude(i => i!.Payments)
            .AsQueryable();
        if (courseId.HasValue)
            query = query.Where(c => c.CourseId == courseId.Value);

        var courses = await query.ToListAsync();

        // اجلب مبلغ الترحيل لكل دورة من الـ ledger عبر ADO.
        var transferredByCourse = new Dictionary<int, decimal>();
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT `CourseId`, COALESCE(SUM(`Amount`),0) FROM `CashboxTransfers` WHERE `CourseId` IS NOT NULL GROUP BY `CourseId`";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var cid = r.GetInt32(0);
                var sum = Convert.ToDecimal(r.GetValue(1));
                transferredByCourse[cid] = sum;
            }
        }
        finally { if (shouldClose) await conn.CloseAsync(); }

        var data = courses.Select(c =>
        {
            var invoices = c.Registrations.Where(r => r.Invoice != null).Select(r => r.Invoice!).ToList();
            var payments = invoices.SelectMany(i => i.Payments).Where(p => !p.IsCancelled).ToList();
            var exemptions = invoices.Sum(i => i.ExemptionAmount);
            var expected = invoices.Sum(i => i.TotalAmount);
            var paid = payments.Sum(p => p.Amount);
            var cash = payments.Where(p => p.PaymentMethod == "Cash").Sum(p => p.Amount);
            var bank = payments.Where(p => p.PaymentMethod != "Cash").Sum(p => p.Amount);
            var transferred = transferredByCourse.TryGetValue(c.CourseId, out var v) ? v : 0m;
            var availableToTransfer = paid - transferred;
            return new CourseRevenueRowViewModel
            {
                CourseId = c.CourseId,
                CourseName = c.CourseName,
                Registrations = c.Registrations.Count(r => r.Status == "Accepted"),
                Original = invoices.Sum(i => i.OriginalAmount),
                Exemptions = exemptions,
                Expected = expected,
                Paid = paid,
                Remaining = expected - paid,
                FullPaid = invoices.Count(i => i.Status == "Paid"),
                Partial = invoices.Count(i => i.Status == "PartiallyPaid"),
                Unpaid = invoices.Count(i => i.Status == "Unpaid"),
                Cash = cash,
                Bank = bank,
                Transferred = transferred,
                AvailableToTransfer = availableToTransfer
            };
        }).ToList();

        ViewBag.Data = data;
        return View();
    }

    public async Task<IActionResult> ExportCourseRevenueXlsx()
    {
        if (!HasPermission("view-reports")) return Forbid();

        var courses = await _db.Courses.Include(c => c.Registrations)
            .ThenInclude(r => r.Invoice).ThenInclude(i => i!.Payments).ToListAsync();

        var transferredByCourse = new Dictionary<int, decimal>();
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT `CourseId`, COALESCE(SUM(`Amount`),0) FROM `CashboxTransfers` WHERE `CourseId` IS NOT NULL GROUP BY `CourseId`";
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                transferredByCourse[r.GetInt32(0)] = Convert.ToDecimal(r.GetValue(1));
        }
        finally { if (shouldClose) await conn.CloseAsync(); }

        using var export = new ExcelExport("إيرادات الدورات",
            "الدورة", "عدد المسجلين", "الرسوم الأصلية", "الإعفاءات", "الإيراد المتوقع", "المدفوع",
            "المتبقي", "سداد كامل", "جزئي", "غير مسدد", "نقدي", "محافظ إلكترونية",
            "مرحَّل للصندوق", "قابل للترحيل");

        foreach (var c in courses)
        {
            var invoices = c.Registrations.Where(r => r.Invoice != null).Select(r => r.Invoice!).ToList();
            var payments = invoices.SelectMany(i => i.Payments).Where(p => !p.IsCancelled).ToList();
            var paid = payments.Sum(p => p.Amount);
            var transferred = transferredByCourse.TryGetValue(c.CourseId, out var v) ? v : 0m;

            export.WriteRow(
                c.CourseName,
                c.Registrations.Count(r => r.Status == "Accepted"),
                invoices.Sum(i => i.OriginalAmount),
                invoices.Sum(i => i.ExemptionAmount),
                invoices.Sum(i => i.TotalAmount),
                paid,
                invoices.Sum(i => i.TotalAmount) - paid,
                invoices.Count(i => i.Status == "Paid"),
                invoices.Count(i => i.Status == "PartiallyPaid"),
                invoices.Count(i => i.Status == "Unpaid"),
                payments.Where(p => p.PaymentMethod == "Cash").Sum(p => p.Amount),
                payments.Where(p => p.PaymentMethod != "Cash").Sum(p => p.Amount),
                transferred,
                paid - transferred);
        }

        return export.Build("course_revenue");
    }
}
