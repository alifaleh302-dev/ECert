using System.Security.Claims;
using ECert.Data;
using ECert.Models;
using ECert.Services;
using ECert.Services.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ECert.Controllers;

/// <summary>
/// Expense management (إدارة المصروفات): submission with up to five images, and the approval queue.
/// Nothing here is course-scoped — an expense belongs to the institution — so the section opens for
/// <c>manage-expenses</c>, <c>view-expenses</c> or <c>approve-expenses</c>. Submitting needs
/// <c>manage-expenses</c>; deciding a pending expense needs <c>approve-expenses</c>, which is what lets
/// a cashier approve without being a SuperAdmin. The submitter may withdraw their own pending request
/// (<see cref="Cancel"/>); the expense categories live in <see cref="ExpenseCategoriesController"/>.
/// </summary>
[Authorize]
public class ExpensesController : Controller
{
    private readonly ECertDbContext _db;
    private readonly ExpenseService _expenses;
    private readonly AuditLogService _audit;
    private readonly IWebHostEnvironment _env;
    private readonly FinancialLedgerService _ledger;

    public ExpensesController(ECertDbContext db, ExpenseService expenses, AuditLogService audit,
        IWebHostEnvironment env, FinancialLedgerService ledger)
    {
        _db = db;
        _expenses = expenses;
        _audit = audit;
        _env = env;
        _ledger = ledger;
    }

    private string Actor => User.Identity?.Name ?? "System";

    private int? CurrentUserId =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    public async Task<IActionResult> Index(int? categoryId, string? status, DateTime? from, DateTime? to)
    {
        if (!User.CanAccessSection("Expenses")) return Forbid();

        var query = _db.Expenses.Include(e => e.Category).Include(e => e.Attachments).AsQueryable();
        if (categoryId.HasValue) query = query.Where(e => e.ExpenseCategoryId == categoryId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
        if (from.HasValue) query = query.Where(e => e.ExpenseDate >= from.Value.Date);
        if (to.HasValue) query = query.Where(e => e.ExpenseDate < to.Value.Date.AddDays(1));

        var expenses = await query.OrderByDescending(e => e.ExpenseDate).ThenByDescending(e => e.ExpenseId)
            .Take(1000).ToListAsync();

        ViewBag.Expenses = expenses;
        ViewBag.Categories = await _db.ExpenseCategories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        ViewBag.CategoryId = categoryId;
        ViewBag.Status = status;
        ViewBag.From = from?.ToString("yyyy-MM-dd");
        ViewBag.To = to?.ToString("yyyy-MM-dd");
        ViewBag.ApprovedTotal = expenses.Where(e => e.Status == ExpenseStatus.Approved).Sum(e => e.Amount);
        ViewBag.PendingCount = expenses.Count(e => e.Status == ExpenseStatus.PendingApproval);
        ViewBag.FundBalance = await _ledger.GetMainFundBalanceAsync();
        ViewBag.CanManage = User.HasPermission(AdminPermissions.ManageExpenses);
        ViewBag.CanApprove = User.HasPermission(AdminPermissions.ApproveExpenses);
        ViewBag.CanManageCategories = User.CanAccessSection("ExpenseCategories");
        ViewBag.CurrentUserId = CurrentUserId;
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenses)) return Forbid();
        var categories = await _db.ExpenseCategories.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToListAsync();
        ViewBag.Categories = categories;
        ViewBag.FundBalance = await _ledger.GetMainFundBalanceAsync();
        ViewBag.CategoryRemaining = await BuildCategoryRemainingAsync(categories);
        return View();
    }

    /// <summary>Remaining budget per category id; a null value means the category has no cap.</summary>
    private async Task<Dictionary<int, decimal?>> BuildCategoryRemainingAsync(List<ExpenseCategory> categories)
    {
        var map = new Dictionary<int, decimal?>();
        foreach (var c in categories)
            map[c.ExpenseCategoryId] = await _expenses.GetCategoryRemainingAsync(c);
        return map;
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int expenseCategoryId, string description, decimal amount,
        DateTime? expenseDate, string? documentNumber, List<IFormFile>? attachments)
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenses)) return Forbid();

        var files = (attachments ?? new List<IFormFile>())
            .Where(f => f != null && f.Length > 0)
            .ToList();

        if (files.Count > ExpenseAttachment.MaxPerExpense)
        {
            TempData["Error"] = $"يمكن إرفاق {ExpenseAttachment.MaxPerExpense} صور كحد أقصى.";
            return RedirectToAction(nameof(Create));
        }

        var uploadedUrls = new List<string>();
        var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "expenses");
        foreach (var file in files)
        {
            var (valid, error) = await ImageUploadValidator.ValidateAsync(file);
            if (!valid)
            {
                DeleteUploads(uploadedUrls);
                TempData["Error"] = error;
                return RedirectToAction(nameof(Create));
            }

            Directory.CreateDirectory(uploadsDir);
            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            using var stream = new FileStream(Path.Combine(uploadsDir, fileName), FileMode.Create);
            await file.CopyToAsync(stream);
            uploadedUrls.Add($"/uploads/expenses/{fileName}");
        }

        var result = await _expenses.SubmitAsync(expenseCategoryId, description, amount,
            expenseDate ?? DateTime.Now, documentNumber, uploadedUrls, Actor, CurrentUserId);

        if (!result.Success)
        {
            // The expense was not recorded, so the images just written would be orphaned.
            DeleteUploads(uploadedUrls);
            TempData["Error"] = result.Message;
            return RedirectToAction(nameof(Create));
        }

        await _audit.LogAsync(Actor, "Create", "Expense", result.Expense!.ExpenseId, null,
            $"Expense: {amount:N2}; Category: {expenseCategoryId}; Attachments: {uploadedUrls.Count}");
        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Best-effort removal of already-written uploads when the submission is abandoned.</summary>
    private void DeleteUploads(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            try
            {
                var path = Path.Combine(_env.WebRootPath, url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
            catch
            {
                // A leftover file on disk is harmless; never fail the request over cleanup.
            }
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(int id)
    {
        var userId = CurrentUserId;
        if (userId == null) return Forbid();

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LockExpenseRowAsync(id);
        var expense = await _db.Expenses.FirstOrDefaultAsync(e => e.ExpenseId == id);
        if (expense == null) return NotFound();

        var result = await _expenses.CancelAsync(expense, userId.Value, Actor);
        if (!result.Success)
        {
            await transaction.RollbackAsync();
            TempData["Error"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        await _audit.LogAsync(Actor, "Cancel", "Expense", expense.ExpenseId, null, $"Cancelled expense: {expense.Amount:N2}");
        await transaction.CommitAsync();
        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? note)
    {
        if (!User.HasPermission(AdminPermissions.ApproveExpenses)) return Forbid();

        // Two approvers can act at once; serialise on the expense row and re-read inside the
        // transaction so a second request sees the already-approved status and writes nothing
        // (no duplicate audit row, no second movement).
        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LockExpenseRowAsync(id);
        var expense = await _db.Expenses.FirstOrDefaultAsync(e => e.ExpenseId == id);
        if (expense == null) return NotFound();

        var result = await _expenses.ApproveAsync(expense, Actor, note);
        if (!result.Success)
        {
            await transaction.RollbackAsync();
            TempData["Error"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        await _audit.LogAsync(Actor, "Approve", "Expense", expense.ExpenseId, null, $"Approved expense: {expense.Amount:N2}");
        await transaction.CommitAsync();
        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(int id, string? note)
    {
        if (!User.HasPermission(AdminPermissions.ApproveExpenses)) return Forbid();

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await LockExpenseRowAsync(id);
        var expense = await _db.Expenses.FirstOrDefaultAsync(e => e.ExpenseId == id);
        if (expense == null) return NotFound();

        var result = await _expenses.RejectAsync(expense, Actor, note);
        if (!result.Success)
        {
            await transaction.RollbackAsync();
            TempData["Error"] = result.Message;
            return RedirectToAction(nameof(Index));
        }

        await _audit.LogAsync(Actor, "Reject", "Expense", expense.ExpenseId, null, $"Rejected expense: {expense.Amount:N2}");
        await transaction.CommitAsync();
        TempData["Success"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Takes an exclusive row lock so concurrent approvals on one expense serialise.</summary>
    private async Task LockExpenseRowAsync(int expenseId)
    {
        var connection = _db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "SELECT `ExpenseId` FROM `Expenses` WHERE `ExpenseId` = @id FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = expenseId;
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync();
    }

    // ===== Expense categories have moved to ExpenseCategoriesController =====

    public async Task<IActionResult> ExportXlsx(int? categoryId, string? status, DateTime? from, DateTime? to)
    {
        if (!User.CanAccessSection("Expenses")) return Forbid();

        var query = _db.Expenses.Include(e => e.Category).AsQueryable();
        if (categoryId.HasValue) query = query.Where(e => e.ExpenseCategoryId == categoryId.Value);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(e => e.Status == status);
        if (from.HasValue) query = query.Where(e => e.ExpenseDate >= from.Value.Date);
        if (to.HasValue) query = query.Where(e => e.ExpenseDate < to.Value.Date.AddDays(1));

        var expenses = await query.OrderByDescending(e => e.ExpenseDate).ToListAsync();
        using var export = new ExcelExport("المصروفات",
            "التاريخ", "البند", "البيان", "المبلغ", "رقم المستند", "الحالة", "قدّمه", "اعتمده");
        foreach (var e in expenses)
        {
            export.WriteRow(e.ExpenseDate.ToString("yyyy-MM-dd"), e.Category?.Name, e.Description, e.Amount,
                e.DocumentNumber, StatusLabel(e.Status), e.SubmittedBy, e.ReviewedBy);
        }
        return export.Build("expenses");
    }

    public static string StatusLabel(string status) => status switch
    {
        ExpenseStatus.Approved => "معتمد",
        ExpenseStatus.Rejected => "مرفوض",
        ExpenseStatus.PendingApproval => "بانتظار الاعتماد",
        ExpenseStatus.Cancelled => "ملغى",
        _ => status
    };
}
