using System.Security.Claims;
using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Employee cash custody (عهدة الموظف) and the handover workflow to the main fund
/// (توريد / ترحيل للصندوق الرئيسي).
///
/// A financial employee sees their own custody balance and ledger, and can request handing over
/// part or all of it. Only a SuperAdmin reviews those requests: approval debits the employee's
/// custody and credits the main fund in a single transaction, so the money is never counted twice
/// and never disappears. A rejected request changes nothing.
/// </summary>
[Authorize]
public class CustodyController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly FinancialLedgerService _ledger;

    public CustodyController(ECertDbContext db, AuditLogService audit, FinancialLedgerService ledger)
    {
        _db = db;
        _audit = audit;
        _ledger = ledger;
    }

    private bool HasPermission(string perm) => User.HasPermission(perm);
    private int? CurrentUserId =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private string Actor => User.Identity?.Name ?? "System";

    /// <summary>
    /// The custody page. A SuperAdmin sees every employee's custody and the pending handover queue;
    /// everyone else sees only their own custody and ledger. The movements table is filtered by entry
    /// type, date range and free text, and paged 20 rows at a time.
    /// </summary>
    public async Task<IActionResult> Index(int? userId, string? entryType, DateTime? from, DateTime? to,
        string? search, int page = 1)
    {
        if (!HasPermission(AdminPermissions.ViewCustody)) return Forbid();

        var isSuperAdmin = User.IsSuperAdmin();

        // The employee whose custody is displayed: an explicit selection (SuperAdmin only),
        // otherwise the signed-in user.
        var targetUserId = isSuperAdmin && userId.HasValue ? userId.Value : CurrentUserId;
        if (!targetUserId.HasValue) return Forbid();

        var target = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserId == targetUserId.Value);
        if (target == null) return NotFound();

        var movements = _db.CustodyEntries.Where(e => e.UserId == targetUserId.Value);
        if (!string.IsNullOrWhiteSpace(entryType)) movements = movements.Where(e => e.EntryType == entryType);
        if (from.HasValue) movements = movements.Where(e => e.CreatedAt >= from.Value.Date);
        if (to.HasValue) movements = movements.Where(e => e.CreatedAt < to.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            movements = movements.Where(e => (e.Note != null && e.Note.Contains(term))
                || (e.ReferenceNumber != null && e.ReferenceNumber.Contains(term))
                || (e.ReferenceType != null && e.ReferenceType.Contains(term)));
        }

        const int pageSize = 20;
        var totalMovements = await movements.CountAsync();
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalMovements / (double)pageSize));
        if (page < 1) page = 1;
        if (page > totalPages) page = totalPages;

        var entries = await movements
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.CustodyEntryId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        ViewBag.TargetUser = target;
        ViewBag.Entries = entries;
        ViewBag.Balance = await _ledger.GetCustodyBalanceAsync(targetUserId.Value);
        ViewBag.IsSuperAdmin = isSuperAdmin;
        ViewBag.EntryType = entryType;
        ViewBag.From = from?.ToString("yyyy-MM-dd");
        ViewBag.To = to?.ToString("yyyy-MM-dd");
        ViewBag.Search = search;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.TotalMovements = totalMovements;
        ViewBag.TotalPages = totalPages;

        // The employee's own pending handover requests, and — for a SuperAdmin — the full queue.
        ViewBag.MyPending = await _db.CustodyHandovers
            .Where(h => h.UserId == targetUserId.Value && h.Status == CustodyHandoverStatus.Pending)
            .OrderByDescending(h => h.RequestedAt)
            .ToListAsync();

        ViewBag.MyHandovers = await _db.CustodyHandovers
            .Where(h => h.UserId == targetUserId.Value)
            .OrderByDescending(h => h.RequestedAt)
            .Take(100)
            .ToListAsync();

        if (isSuperAdmin)
        {
            ViewBag.Employees = await _db.CustodyEntries
                .GroupBy(e => e.UserId)
                .Select(g => new { UserId = g.Key, Balance = g.Sum(e => e.Amount) })
                .Join(_db.Users, g => g.UserId, u => u.UserId, (g, u) => new CustodySummaryRow
                {
                    UserId = u.UserId,
                    FullName = u.FullName,
                    Username = u.Username,
                    Balance = g.Balance
                })
                .OrderByDescending(r => r.Balance)
                .ToListAsync();

            ViewBag.PendingHandovers = await _db.CustodyHandovers
                .Where(h => h.Status == CustodyHandoverStatus.Pending)
                .Include(h => h.User)
                .OrderByDescending(h => h.RequestedAt)
                .ToListAsync();

            ViewBag.TotalCustody = await _ledger.GetTotalCustodyAsync();
        }

        return View();
    }

    /// <summary>An employee requests handing over part or all of their custody to the main fund.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestHandover(decimal amount, string? note)
    {
        if (!HasPermission(AdminPermissions.ViewCustody)) return Forbid();
        if (!CurrentUserId.HasValue) return Forbid();

        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0)
        {
            TempData["Error"] = "المبلغ يجب أن يكون أكبر من صفر.";
            return RedirectToAction(nameof(Index));
        }

        var balance = await _ledger.GetCustodyBalanceAsync(CurrentUserId.Value);
        if (amount > balance)
        {
            TempData["Error"] = $"المبلغ المطلوب ({amount:N2}) أكبر من رصيد عهدتك الحالي ({balance:N2}).";
            return RedirectToAction(nameof(Index));
        }

        // Only one open request at a time, so the approver is never asked to release money twice.
        var hasPending = await _db.CustodyHandovers.AnyAsync(h =>
            h.UserId == CurrentUserId.Value && h.Status == CustodyHandoverStatus.Pending);
        if (hasPending)
        {
            TempData["Error"] = "لديك طلب توريد قيد المراجعة بالفعل. انتظر اعتماده أو رفضه.";
            return RedirectToAction(nameof(Index));
        }

        _db.CustodyHandovers.Add(new CustodyHandover
        {
            UserId = CurrentUserId.Value,
            Amount = amount,
            Status = CustodyHandoverStatus.Pending,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            RequestedBy = Actor,
            RequestedAt = DateTime.Now
        });
        await _db.SaveChangesAsync();
        await _audit.LogAsync(Actor, "Create", "CustodyHandover", null, null, $"Handover request: {amount:N2}");

        TempData["Success"] = $"تم تقديم طلب توريد بمبلغ {amount:N2} ريال للصندوق الرئيسي، وهو بانتظار اعتماد مدير النظام.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>SuperAdmin approves a handover: the custody is debited and the main fund credited.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? note)
    {
        if (!User.IsSuperAdmin()) return Forbid();

        var handover = await _db.CustodyHandovers.Include(h => h.User).FirstOrDefaultAsync(h => h.CustodyHandoverId == id);
        if (handover == null) return NotFound();
        if (handover.Status != CustodyHandoverStatus.Pending)
        {
            TempData["Error"] = "تمت مراجعة هذا الطلب مسبقاً.";
            return RedirectToAction(nameof(Index));
        }

        var balance = await _ledger.GetCustodyBalanceAsync(handover.UserId);
        if (handover.Amount > balance)
        {
            TempData["Error"] = $"رصيد العهدة الحالي ({balance:N2}) أقل من المبلغ المطلوب ({handover.Amount:N2}). رفض الطلب أو انتظر تسويات جديدة.";
            return RedirectToAction(nameof(Index), new { userId = handover.UserId });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();

        // Debit the employee's custody (this is also the audit row for the handover) and move the
        // amount into the main fund as a general transfer, in one atomic step.
        _ledger.AddHandoverCustody(handover.UserId, handover.Amount, Actor, handover.CustodyHandoverId,
            $"توريد عهدة معتمد — {handover.User?.FullName ?? handover.RequestedBy}");

        _db.CashboxTransfers.Add(new CashboxTransfer
        {
            CourseId = null,
            Amount = handover.Amount,
            Note = $"توريد عهدة الموظف {handover.User?.FullName ?? handover.RequestedBy} (طلب #{handover.CustodyHandoverId})",
            CreatedBy = Actor,
            CreatedAt = DateTime.Now
        });

        handover.Status = CustodyHandoverStatus.Approved;
        handover.ReviewedBy = Actor;
        handover.ReviewedAt = DateTime.Now;
        handover.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        await _audit.LogAsync(Actor, "Approve", "CustodyHandover", handover.CustodyHandoverId, null,
            $"Approved handover: {handover.Amount:N2}");

        TempData["Success"] = $"تم اعتماد توريد {handover.Amount:N2} ريال إلى الصندوق الرئيسي.";
        return RedirectToAction(nameof(Index), new { userId = handover.UserId });
    }

    /// <summary>SuperAdmin rejects a handover; nothing moves.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? note)
    {
        if (!User.IsSuperAdmin()) return Forbid();

        var handover = await _db.CustodyHandovers.FirstOrDefaultAsync(h => h.CustodyHandoverId == id);
        if (handover == null) return NotFound();
        if (handover.Status != CustodyHandoverStatus.Pending)
        {
            TempData["Error"] = "تمت مراجعة هذا الطلب مسبقاً.";
            return RedirectToAction(nameof(Index));
        }

        handover.Status = CustodyHandoverStatus.Rejected;
        handover.ReviewedBy = Actor;
        handover.ReviewedAt = DateTime.Now;
        handover.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        await _db.SaveChangesAsync();
        await _audit.LogAsync(Actor, "Reject", "CustodyHandover", handover.CustodyHandoverId, null,
            $"Rejected handover: {handover.Amount:N2}");

        TempData["Success"] = "تم رفض طلب التوريد.";
        return RedirectToAction(nameof(Index));
    }
}

/// <summary>Row of the SuperAdmin custody-summary table (one per employee holding custody).</summary>
public class CustodySummaryRow
{
    public int UserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public decimal Balance { get; set; }
}
