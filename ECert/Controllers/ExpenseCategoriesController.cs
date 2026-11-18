using ECert.Data;
using ECert.Models;
using ECert.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ECert.Controllers;

/// <summary>
/// Expense categories (بنود المصروفات): the chart of expense lines with an optional budget cap each.
/// Split from <see cref="ExpensesController"/> so the institution can hand a bookkeeper the right to
/// shape the chart without granting the right to record or approve expenses: reading the list needs
/// <c>view-expense-categories</c> and changing it needs <c>manage-expense-categories</c>.
/// </summary>
[Authorize]
public class ExpenseCategoriesController : Controller
{
    private readonly ECertDbContext _db;
    private readonly ExpenseService _expenses;
    private readonly AuditLogService _audit;

    public ExpenseCategoriesController(ECertDbContext db, ExpenseService expenses, AuditLogService audit)
    {
        _db = db;
        _expenses = expenses;
        _audit = audit;
    }

    private string Actor => User.Identity?.Name ?? "System";

    public async Task<IActionResult> Index()
    {
        if (!User.CanAccessSection("ExpenseCategories")) return Forbid();

        var categories = await _db.ExpenseCategories.OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ToListAsync();
        ViewBag.Categories = categories;
        // Spent per category counts every non-rejected, non-cancelled expense, matching the cap rule.
        ViewBag.Spent = await _db.Expenses
            .Where(e => e.Status != ExpenseStatus.Rejected && e.Status != ExpenseStatus.Cancelled)
            .GroupBy(e => e.ExpenseCategoryId)
            .Select(g => new { CategoryId = g.Key, Total = g.Sum(e => e.Amount) })
            .ToDictionaryAsync(x => x.CategoryId, x => x.Total);
        ViewBag.CanManage = User.HasPermission(AdminPermissions.ManageExpenseCategories);
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(int? expenseCategoryId, string name, int sortOrder,
        bool isActive = true, decimal? maxAmount = null)
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenseCategories)) return Forbid();

        var result = await _expenses.SaveCategoryAsync(expenseCategoryId, name, sortOrder, isActive, maxAmount);
        if (result.Success)
            await _audit.LogAsync(Actor, expenseCategoryId.HasValue ? "Update" : "Create", "ExpenseCategory",
                expenseCategoryId, null, name);

        TempData[result.Success ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(int expenseCategoryId)
    {
        if (!User.HasPermission(AdminPermissions.ManageExpenseCategories)) return Forbid();

        var result = await _expenses.DeleteCategoryAsync(expenseCategoryId);
        if (result.Success)
            await _audit.LogAsync(Actor, "Delete", "ExpenseCategory", expenseCategoryId);

        TempData[result.Success ? "Success" : "Error"] = result.Message;
        return RedirectToAction(nameof(Index));
    }
}
