using System.Data;
using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>واجهة الصندوق — ثلاث عمليات:
/// 1) واجهة ترحيل الرصيد (متاحة لمدير المركز والمالي والـ SuperAdmin).
/// 2) واجهة الصندوق تَعرض كل الترحيلات والسحوبات وإجمالي الصندوق (لمدير المركز والـ SuperAdmin).
/// 3) سحب من الصندوق (SuperAdmin فقط).
///
/// كل المبالغ تُشتق من الـ Ledger في وقت الاستعلام، لا توجد حقل متغيّر.
/// </summary>
[Authorize]
public class CashboxController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    public CashboxController(ECertDbContext db, AuditLogService audit)
    { _db = db; _audit = audit; }

    private bool IsSuperAdmin => User.IsInRole(CashboxRoles.SystemManager);
    private bool IsManager => User.IsInRole(CashboxRoles.Manager);
    private bool CanTransfer => IsSuperAdmin
        || User.HasClaim(c => c.Type == "Permission" && c.Value == CashboxRoles.PermTransfer);
    private bool CanWithdraw => IsSuperAdmin
        || User.HasClaim(c => c.Type == "Permission" && c.Value == CashboxRoles.PermWithdraw);

    /// <summary>منظر الصندوق الشامل — يعرض إجمالي الصندوق، إجمالي الرصيد بدون ترحيل،
    /// السحوبات، وعمليات الترحيل المجمَّعة حسب الدورة.</summary>
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        if (!IsSuperAdmin && !IsManager && !CanTransfer) return Forbid();

        // جلب كل الترحيلات والسحوبات عبر ADO.NET لتجاوز sqlite/pomelo edge case في EF.
        var transfers = new List<CashboxTransfer>();
        var withdrawals = new List<CashboxWithdrawal>();
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT `Id`,`CourseId`,`Amount`,COALESCE(`Note`,''),`CreatedBy`,`CreatedAt` FROM `CashboxTransfers` ORDER BY `CreatedAt` DESC";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    transfers.Add(new CashboxTransfer
                    {
                        Id = r.GetInt32(0),
                        CourseId = r.IsDBNull(1) ? null : r.GetInt32(1),
                        Amount = r.GetDecimal(2),
                        Note = r.GetString(3),
                        CreatedBy = r.GetString(4),
                        CreatedAt = r.GetDateTime(5)
                    });
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT `Id`,`Amount`,`Reason`,`CreatedBy`,`CreatedAt` FROM `CashboxWithdrawals` ORDER BY `CreatedAt` DESC";
                using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    withdrawals.Add(new CashboxWithdrawal
                    {
                        Id = r.GetInt32(0),
                        Amount = r.GetDecimal(1),
                        Reason = r.GetString(2),
                        CreatedBy = r.GetString(3),
                        CreatedAt = r.GetDateTime(4)
                    });
            }
        }
        finally { if (shouldClose) await conn.CloseAsync(); }

        // إجماليات
        decimal totalTransferred = transfers.Sum(t => t.Amount);
        decimal totalWithdrawn = withdrawals.Sum(w => w.Amount);
        decimal totalCollected = 0m; // مجموع المُحصل من كل الدورات
        var collected = await _db.Payments.IgnoreQueryFilters().Where(p => !p.IsCancelled)
            .Select(p => (double?)p.Amount).ToListAsync();
        if (collected.Any()) totalCollected = (decimal)collected.Sum();

        // رصيد كل دورة خارج الصندوق
        var byCourse = transfers
            .Where(t => t.CourseId.HasValue)
            .GroupBy(t => t.CourseId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));

        ViewBag.Transfers = transfers;
        ViewBag.Withdrawals = withdrawals;
        ViewBag.TotalCollected = totalCollected;
        ViewBag.TotalTransferred = totalTransferred;
        ViewBag.TotalWithdrawn = totalWithdrawn;
        ViewBag.FundRemaining = totalTransferred - totalWithdrawn;
        ViewBag.UntransferredTotal = totalCollected - totalTransferred;
        ViewBag.TransfersByCourse = byCourse;
        // Razor iterates ViewBag values as dynamic; anonymous-type properties are
        // inaccessible to the generated view and cause RuntimeBinderException.
        ViewBag.Courses = await _db.Courses.OrderBy(c => c.CourseName).ToListAsync();
        ViewBag.CanTransfer = CanTransfer;
        ViewBag.CanWithdraw = CanWithdraw;
        ViewBag.IsSuperAdmin = IsSuperAdmin;
        ViewBag.IsManager = IsManager;
        return View();
    }

    /// <summary>تنفيذ عملية ترحيل رصيد دورة إلى الصندوق.
    /// يفرض: صلاحية ترحيل، مبلغ > 0، رصيد الدورة المدفوع > الرصيد المرحل منها (لا يمكن ترحيل مبلغ أكبر من المتاح).
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Transfer(int courseId, decimal amount, string? note)
    {
        if (!CanTransfer) return Forbid();

        if (amount <= 0)
        {
            TempData["Error"] = "المبلغ يجب أن يكون أكبر من صفر.";
            return RedirectToAction("CourseRevenue", "Reports");
        }

        // اجلب الدورة وأحسب PaidAmount و TransferredAmount يدوياً ثم قارن.
        var course = await _db.Courses
            .Include(c => c.Registrations).ThenInclude(r => r.Invoice).ThenInclude(i => i!.Payments)
            .FirstOrDefaultAsync(c => c.CourseId == courseId);
        if (course == null)
        {
            TempData["Error"] = "الدورة غير موجودة.";
            return RedirectToAction("CourseRevenue", "Reports");
        }

        var invoices = course.Registrations.Where(r => r.Invoice != null).Select(r => r.Invoice!).ToList();
        var collected = invoices.SelectMany(i => i.Payments).Where(p => !p.IsCancelled).Sum(p => p.Amount);

        // قراءة SUM(Amount) لعمليات ترحيل هذه الدورة
        decimal alreadyTransferred = 0m;
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COALESCE(SUM(`Amount`),0) FROM `CashboxTransfers` WHERE `CourseId`=@cid";
            var p = cmd.CreateParameter(); p.ParameterName = "@cid"; p.Value = courseId; cmd.Parameters.Add(p);
            var v = await cmd.ExecuteScalarAsync();
            if (v != null && v != DBNull.Value) alreadyTransferred = Convert.ToDecimal(v);
        }
        finally { if (shouldClose) await conn.CloseAsync(); }

        var available = collected - alreadyTransferred;
        if (amount > available)
        {
            TempData["Error"] = $"المبلغ المُرحَّل ({amount:N2}) أكبر من الرصيد المتاح ({available:N2}) لهذه الدورة.";
            return RedirectToAction("CourseRevenue", "Reports");
        }

        var actor = User.Identity?.Name ?? CashboxRoles.SystemManager;
        var entry = new CashboxTransfer
        {
            CourseId = courseId,
            Amount = amount,
            Note = note,
            CreatedBy = actor,
            CreatedAt = DateTime.Now
        };
        _db.CashboxTransfers.Add(entry);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(actor, "Transfer", "Cashbox", entry.Id, null,
            $"دورة {course.CourseName} — مبلغ {amount:N2}");

        TempData["Success"] = $"تم ترحيل {amount:N2} ريال من دورة «{course.CourseName}» إلى الصندوق.";
        return RedirectToAction("CourseRevenue", "Reports",
            new { courseId });
    }

    /// <summary>سحب من الصندوق — SuperAdmin فقط.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Withdraw(decimal amount, string reason)
    {
        if (!CanWithdraw) return Forbid();

        if (amount <= 0)
        {
            TempData["Error"] = "مبلغ السحب يجب أن يكون أكبر من صفر.";
            return RedirectToAction("Index");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            TempData["Error"] = "يجب ذكر سبب السحب.";
            return RedirectToAction("Index");
        }

        // الصندوق المتاح = إجمالي الترحيلات − إجمالي السحوبات
        decimal totalTransferred = 0m, totalWithdrawn = 0m;
        var conn = _db.Database.GetDbConnection();
        var shouldClose = conn.State != ConnectionState.Open;
        if (shouldClose) await conn.OpenAsync();
        try
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(`Amount`),0) FROM `CashboxTransfers`";
                var v = await cmd.ExecuteScalarAsync();
                if (v != null && v != DBNull.Value) totalTransferred = Convert.ToDecimal(v);
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COALESCE(SUM(`Amount`),0) FROM `CashboxWithdrawals`";
                var v = await cmd.ExecuteScalarAsync();
                if (v != null && v != DBNull.Value) totalWithdrawn = Convert.ToDecimal(v);
            }
        }
        finally { if (shouldClose) await conn.CloseAsync(); }

        var available = totalTransferred - totalWithdrawn;
        if (amount > available)
        {
            TempData["Error"] = $"مبلغ السحب ({amount:N2}) أكبر من الصندوق المتاح ({available:N2}).";
            return RedirectToAction("Index");
        }

        var actor = User.Identity?.Name ?? CashboxRoles.SystemManager;
        var entry = new CashboxWithdrawal
        {
            Amount = amount,
            Reason = reason.Trim(),
            CreatedBy = actor,
            CreatedAt = DateTime.Now
        };
        _db.CashboxWithdrawals.Add(entry);
        await _db.SaveChangesAsync();

        await _audit.LogAsync(actor, "Withdraw", "Cashbox", entry.Id, null,
            $"سحب {amount:N2} ريال — {reason}");

        TempData["Success"] = $"تم تسجيل سحب {amount:N2} ريال من الصندوق.";
        return RedirectToAction("Index");
    }
}
