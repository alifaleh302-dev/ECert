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
/// part or all of it. Someone holding <c>approve-custody</c> reviews those requests: approval debits
/// the employee's custody and credits the main fund in a single transaction, so the money is never
/// counted twice and never disappears. A rejected request changes nothing.
/// </summary>
[Authorize]
public class CustodyController : Controller
{
    private readonly ECertDbContext _db;
    private readonly AuditLogService _audit;
    private readonly FinancialLedgerService _ledger;
    private readonly FinancialService _financial;
    private readonly SystemSettingsService _settings;

    public CustodyController(ECertDbContext db, AuditLogService audit, FinancialLedgerService ledger,
        FinancialService financial, SystemSettingsService settings)
    {
        _db = db;
        _audit = audit;
        _ledger = ledger;
        _financial = financial;
        _settings = settings;
    }

    private bool HasPermission(string perm) => User.HasPermission(perm);
    private int? CurrentUserId =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;
    private string Actor => User.Identity?.Name ?? "System";

    /// <summary>May review every employee's custody and decide handover requests.</summary>
    private bool CanReviewAll => HasPermission(AdminPermissions.ApproveCustody);

    /// <summary>
    /// Requesting a handover is a write: it is the collector handing money back. Read-only roles
    /// (view-custody only) must not reach it, so it additionally needs a collecting permission.
    /// </summary>
    private bool CanRequestHandover => HasPermission(AdminPermissions.ViewCustody)
        && User.HasAnyPermission(AdminPermissions.ManagePayments, AdminPermissions.ManageRefunds, AdminPermissions.ManageCashbox);

    /// <summary>
    /// The custody page. An approver sees every employee's custody and the pending handover queue;
    /// everyone else sees only their own custody and ledger. The movements table is filtered by entry
    /// type, date range and free text, and paged 20 rows at a time.
    /// </summary>
    public async Task<IActionResult> Index(int? userId, string? entryType, DateTime? from, DateTime? to,
        string? search, int page = 1)
    {
        if (!User.HasAnyPermission(AdminPermissions.ViewCustody, AdminPermissions.ApproveCustody)) return Forbid();

        var canReviewAll = CanReviewAll;

        // The employee whose custody is displayed: an explicit selection (reviewer only),
        // otherwise the signed-in user.
        var targetUserId = canReviewAll && userId.HasValue ? userId.Value : CurrentUserId;
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
        ViewBag.CanReview = canReviewAll;
        ViewBag.CanRequestHandover = CanRequestHandover;
        ViewBag.IsSuperAdmin = canReviewAll;
        ViewBag.EntryType = entryType;
        ViewBag.From = from?.ToString("yyyy-MM-dd");
        ViewBag.To = to?.ToString("yyyy-MM-dd");
        ViewBag.Search = search;
        ViewBag.Page = page;
        ViewBag.PageSize = pageSize;
        ViewBag.TotalMovements = totalMovements;
        ViewBag.TotalPages = totalPages;

        // The employee's own pending handover requests, and — for a reviewer — the full queue.
        ViewBag.MyPending = await _db.CustodyHandovers
            .Where(h => h.UserId == targetUserId.Value && h.Status == CustodyHandoverStatus.Pending)
            .OrderByDescending(h => h.RequestedAt)
            .ToListAsync();

        ViewBag.MyHandovers = await _db.CustodyHandovers
            .Where(h => h.UserId == targetUserId.Value)
            .OrderByDescending(h => h.RequestedAt)
            .Take(100)
            .ToListAsync();

        if (canReviewAll)
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
        if (!CanRequestHandover) return Forbid();
        if (!CurrentUserId.HasValue) return Forbid();

        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0)
        {
            TempData["Error"] = "المبلغ يجب أن يكون أكبر من صفر.";
            return RedirectToAction(nameof(Index));
        }

        // A handover may only draw on custody not already claimed by another pending movement (a
        // refund request or an earlier handover), so two requests can never promise the same money.
        var balance = await _ledger.GetCustodyBalanceAsync(CurrentUserId.Value);
        var reserved = await _financial.GetReservedCustodyAsync(CurrentUserId.Value);
        var free = balance - reserved;
        if (amount > free)
        {
            TempData["Error"] = $"المبلغ المطلوب ({amount:N2}) أكبر من رصيد عهدتك المتاح ({free:N2})" +
                (reserved > 0 ? $" — منه {reserved:N2} محجوز لطلبات معلّقة." : ".");
            return RedirectToAction(nameof(Index));
        }

        // A configurable cap on how many handover requests an employee may keep open at once, so the
        // approver is never asked to release the same custody several times over.
        var maxOpen = await _settings.GetMaxOpenHandoversPerEmployeeAsync();
        var openCount = await _db.CustodyHandovers.CountAsync(h =>
            h.UserId == CurrentUserId.Value && h.Status == CustodyHandoverStatus.Pending);
        if (openCount >= maxOpen)
        {
            TempData["Error"] = maxOpen <= 1
                ? "لديك طلب توريد قيد المراجعة بالفعل. انتظر اعتماده أو رفضه."
                : $"لديك {openCount} طلبات توريد قيد المراجعة، والحد الأقصى {maxOpen}. انتظر اعتمادها أو رفضها.";
            return RedirectToAction(nameof(Index));
        }

        var handover = new CustodyHandover
        {
            UserId = CurrentUserId.Value,
            Amount = amount,
            Status = CustodyHandoverStatus.Pending,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            RequestedBy = Actor,
            RequestedAt = DateTime.Now
        };
        _db.CustodyHandovers.Add(handover);
        await _db.SaveChangesAsync();
        await _audit.LogAsync(Actor, "Create", "CustodyHandover", handover.CustodyHandoverId, null, $"Handover request: {amount:N2}");

        // When auto-approval is on, the request settles immediately: the custody is debited and the
        // main fund credited, exactly as if an approver had released it.
        if (await _settings.HandoverAutoApprovalEnabledAsync())
        {
            await ApproveHandoverAsync(handover, note);
            TempData["Success"] = $"تم توريد {amount:N2} {await _settings.GetCurrencySymbolAsync()} إلى الصندوق الرئيسي (اعتماد تلقائي).";
            return RedirectToAction(nameof(Index));
        }

        TempData["Success"] = $"تم تقديم طلب توريد بمبلغ {amount:N2} {await _settings.GetCurrencySymbolAsync()} للصندوق الرئيسي، وهو بانتظار اعتماد مدير النظام.";
        return RedirectToAction(nameof(Index));
    }

    /// <summary>An approver approves a handover: the custody is debited and the main fund credited.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? note)
    {
        if (!CanReviewAll) return Forbid();

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

        await ApproveHandoverAsync(handover, note);

        TempData["Success"] = $"تم اعتماد توريد {handover.Amount:N2} {await _settings.GetCurrencySymbolAsync()} إلى الصندوق الرئيسي.";
        return RedirectToAction(nameof(Index), new { userId = handover.UserId });
    }

    /// <summary>
    /// Settles a pending handover: debits the employee's custody and credits the main fund as one
    /// atomic movement, then marks the request approved. Shared by the manual approval action and by
    /// auto-approval so both paths move the money identically. The caller must have checked that the
    /// custody covers the amount.
    /// </summary>
    private async Task ApproveHandoverAsync(CustodyHandover handover, string? note)
    {
        if (handover.User == null)
            handover.User = await _db.Users.FirstOrDefaultAsync(u => u.UserId == handover.UserId);

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
    }

    /// <summary>An approver rejects a handover; nothing moves.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? note)
    {
        if (!CanReviewAll) return Forbid();

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
