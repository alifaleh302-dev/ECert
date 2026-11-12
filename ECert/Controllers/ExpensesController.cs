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
/// Expense management (إدارة المصروفات): categories, submission with an optional attachment, and the
/// SuperAdmin approval queue. Nothing here is course-scoped — an expense belongs to the institution —
/// so the section is gated by the <c>manage-expenses</c> permission and approval is SuperAdmin-only.
/// </summary>
[Authorize]
public class ExpensesController : Controller
{
    private readonly ECertDbContext _db;
    private readonly ExpenseService _expenses;
    private readonly AuditLogService _audit;
    private readonly IWebHostEnvironment _env;

    public ExpensesController(ECertDbContext db, ExpenseService expenses, AuditLogService audit, IWebHostEnvironment env)
    {
        _db = db;
        _expenses = expenses;
        _audit = audit;
        _env = env;
    }

    private string Actor => User.Identity?.Name ?? "System";

    public async Task<IActionResult> Index(int? categoryId, string? status, DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenses)) return Forbid();

        var query = _db.Expenses.Include(e => e.Category).AsQueryable();
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
        ViewBag.IsSuperAdmin = User.IsSuperAdmin();
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenses)) return Forbid();
        ViewBag.Categories = await _db.ExpenseCategories.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ToListAsync();
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(int expenseCategoryId, string description, decimal amount,
        DateTime? expenseDate, string? documentNumber, IFormFile? attachment)
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenses)) return Forbid();

        string? attachmentUrl = null;
        if (attachment != null && attachment.Length > 0)
        {
            var (valid, error) = await ImageUploadValidator.ValidateAsync(attachment);
            if (!valid)
            {
                TempData["Error"] = error;
                return RedirectToAction(nameof(Create));
            }
            var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "expenses");
            Directory.CreateDirectory(uploadsDir);
            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(attachment.FileName)}";
            using var stream = new FileStream(Path.Combine(uploadsDir, fileName), FileMode.Create);
            await attachment.CopyToAsync(stream);
            attachmentUrl = $"/uploads/expenses/{fileName}";
        }

        var result = await _expenses.SubmitAsync(expenseCategoryId, description, amount,
            expenseDate ?? DateTime.Now, documentNumber, attachmentUrl, Actor);

        TempData[result.Success ? "Success" : "Error"] = result.Message;
        if (!result.Success) return RedirectToAction(nameof(Create));

        await _audit.LogAsync(Actor, "Create", "Expense", result.Expense!.ExpenseId, null,
            $"Expense: {amount:N2}; Category: {expenseCategoryId}");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(int id, string? note)
    {
        if (!User.IsSuperAdmin()) return Forbid();

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
        if (!User.IsSuperAdmin()) return Forbid();

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

    // ===== Expense categories =====

    public async Task<IActionResult> Categories()
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenses)) return Forbid();
        ViewBag.Categories = await _db.ExpenseCategories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        ViewBag.Usage = await _db.Expenses.GroupBy(e => e.ExpenseCategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Count);
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCategory(int? expenseCategoryId, string name, int sortOrder, bool isActive = true)
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenses)) return Forbid();
        if (string.IsNullOrWhiteSpace(name))
        {
            TempData["Error"] = "اسم البند مطلوب.";
            return RedirectToAction(nameof(Categories));
        }

        if (expenseCategoryId.HasValue)
        {
            var existing = await _db.ExpenseCategories.FirstOrDefaultAsync(c => c.ExpenseCategoryId == expenseCategoryId.Value);
            if (existing == null) return NotFound();
            existing.Name = name.Trim();
            existing.SortOrder = sortOrder;
            existing.IsActive = isActive;
        }
        else
        {
            _db.ExpenseCategories.Add(new ExpenseCategory { Name = name.Trim(), SortOrder = sortOrder, IsActive = isActive });
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم حفظ بند المصروف.";
        return RedirectToAction(nameof(Categories));
    }

    public async Task<IActionResult> ExportXlsx(int? categoryId, string? status, DateTime? from, DateTime? to)
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenses)) return Forbid();

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
        _ => status
    };
}
