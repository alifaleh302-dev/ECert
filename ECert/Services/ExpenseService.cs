using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Owns the expense lifecycle. An expense is submitted by a financial employee and must be approved
/// by a SuperAdmin before it counts in the reports; rejection changes nothing. Approval writes an
/// append-only audit row through the ledger so an expense is a first-class financial movement.
///
/// Every category may carry a budget cap (<see cref="ExpenseCategory.MaxAmount"/>). The cap is
/// enforced on both sides of the lifecycle: a submission that would push the category past its cap
/// is refused, and so is an approval that would (so a pending expense cannot be used to overshoot).
/// Non-rejected expenses count toward the cap, because a pending expense is already a committed
/// intention to spend.
/// </summary>
public class ExpenseService
{
    private readonly ECertDbContext _db;
    private readonly FinancialLedgerService _ledger;
    private readonly PeriodLockService _periods;

    public ExpenseService(ECertDbContext db, FinancialLedgerService ledger, PeriodLockService periods)
    {
        _db = db;
        _ledger = ledger;
        _periods = periods;
    }

    public sealed record ExpenseResult(bool Success, string Message, Expense? Expense = null);

    /// <summary>Submits a new expense awaiting approval. The caller owns the transaction.</summary>
    public async Task<ExpenseResult> SubmitAsync(int categoryId, string description, decimal amount,
        DateTime expenseDate, string? documentNumber, string? attachmentUrl, string actor)
    {
        if (string.IsNullOrWhiteSpace(description))
            return new ExpenseResult(false, "بيان المصروف مطلوب.");

        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0)
            return new ExpenseResult(false, "المبلغ يجب أن يكون أكبر من صفر.");

        var category = await _db.ExpenseCategories.FirstOrDefaultAsync(c => c.ExpenseCategoryId == categoryId);
        if (category == null)
            return new ExpenseResult(false, "بند المصروف غير موجود.");
        if (!category.IsActive)
            return new ExpenseResult(false, "البند غير نشط؛ اختر بنداً آخر.");

        if (await _periods.IsLockedAsync(expenseDate))
            return new ExpenseResult(false, (await _periods.BlockedMessageAsync(expenseDate))!);

        if (category.MaxAmount.HasValue)
        {
            var spent = await GetCategorySpentAsync(categoryId);
            if (spent + amount > category.MaxAmount.Value)
                return new ExpenseResult(false,
                    $"يتجاوز هذا المصروف الحد الأقصى لبند «{category.Name}». " +
                    $"الحد: {category.MaxAmount.Value:N2}، المصروف: {spent:N2}، المتبقي: {Math.Max(0m, category.MaxAmount.Value - spent):N2}.");
        }

        var expense = new Expense
        {
            ExpenseCategoryId = categoryId,
            Description = description.Trim(),
            Amount = amount,
            ExpenseDate = expenseDate.Date,
            DocumentNumber = string.IsNullOrWhiteSpace(documentNumber) ? null : documentNumber.Trim(),
            AttachmentUrl = attachmentUrl,
            Status = ExpenseStatus.PendingApproval,
            SubmittedBy = actor,
            SubmittedAt = DateTime.Now
        };
        _db.Expenses.Add(expense);
        await _db.SaveChangesAsync();
        return new ExpenseResult(true, "تم تسجيل المصروف وهو بانتظار اعتماد مدير النظام.", expense);
    }

    /// <summary>Approves a pending expense; it now counts in the reports and is audited.</summary>
    public async Task<ExpenseResult> ApproveAsync(Expense expense, string approver, string? note)
    {
        if (expense.Status != ExpenseStatus.PendingApproval)
            return new ExpenseResult(false, "تمت مراجعة هذا المصروف مسبقاً.");

        if (await _periods.IsLockedAsync(expense.ExpenseDate))
            return new ExpenseResult(false, (await _periods.BlockedMessageAsync(expense.ExpenseDate))!);

        var category = await _db.ExpenseCategories.FirstOrDefaultAsync(c => c.ExpenseCategoryId == expense.ExpenseCategoryId);

        // Re-check the cap excluding this expense itself: between submission and approval other
        // expenses may have consumed the remaining budget, and approving this one would overshoot.
        if (category?.MaxAmount.HasValue == true)
        {
            var spentOthers = await GetCategorySpentAsync(expense.ExpenseCategoryId, excludeExpenseId: expense.ExpenseId);
            if (spentOthers + expense.Amount > category.MaxAmount!.Value)
                return new ExpenseResult(false,
                    $"لا يمكن الاعتماد: سيؤدي إلى تجاوز الحد الأقصى لبند «{category.Name}». " +
                    $"الحد: {category.MaxAmount.Value:N2}، المصروف بالبند: {spentOthers:N2}.");
        }

        expense.Status = ExpenseStatus.Approved;
        expense.ReviewedBy = approver;
        expense.ReviewedAt = DateTime.Now;
        expense.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        _ledger.AddAudit(approver, FinancialActionTypes.Expense, expense.Amount,
            referenceType: nameof(Expense), referenceId: expense.ExpenseId, referenceNumber: expense.DocumentNumber,
            details: $"اعتماد مصروف — {category?.Name ?? "بند"}: {expense.Description}");

        await _db.SaveChangesAsync();
        return new ExpenseResult(true, "تم اعتماد المصروف.", expense);
    }

    /// <summary>Rejects a pending expense; nothing is counted.</summary>
    public async Task<ExpenseResult> RejectAsync(Expense expense, string approver, string? note)
    {
        if (expense.Status != ExpenseStatus.PendingApproval)
            return new ExpenseResult(false, "تمت مراجعة هذا المصروف مسبقاً.");

        expense.Status = ExpenseStatus.Rejected;
        expense.ReviewedBy = approver;
        expense.ReviewedAt = DateTime.Now;
        expense.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        await _db.SaveChangesAsync();
        return new ExpenseResult(true, "تم رفض المصروف.", expense);
    }

    // ===== Category budget accounting =====

    /// <summary>Sum of the amounts spent under a category. Rejected expenses are ignored.</summary>
    public async Task<decimal> GetCategorySpentAsync(int categoryId, int? excludeExpenseId = null)
    {
        var query = _db.Expenses.Where(e => e.ExpenseCategoryId == categoryId && e.Status != ExpenseStatus.Rejected);
        if (excludeExpenseId.HasValue) query = query.Where(e => e.ExpenseId != excludeExpenseId.Value);
        return await query.SumAsync(e => (decimal?)e.Amount) ?? 0m;
    }

    /// <summary>The remaining budget for a category, or <c>null</c> when the category has no cap.</summary>
    public async Task<decimal?> GetCategoryRemainingAsync(ExpenseCategory category)
    {
        if (!category.MaxAmount.HasValue) return null;
        var spent = await GetCategorySpentAsync(category.ExpenseCategoryId);
        return category.MaxAmount.Value - spent;
    }

    // ===== Category management =====

    /// <summary>Creates or updates an expense category (بند مصروف). Returns the category id.</summary>
    public async Task<ExpenseResult> SaveCategoryAsync(int? categoryId, string name, int sortOrder,
        bool isActive, decimal? maxAmount)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new ExpenseResult(false, "اسم البند مطلوب.");
        if (maxAmount.HasValue && maxAmount.Value <= 0)
            return new ExpenseResult(false, "الحد الأقصى يجب أن يكون أكبر من صفر (أو اتركه فارغاً لبلا حد).");

        ExpenseCategory category;
        if (categoryId.HasValue)
        {
            var existing = await _db.ExpenseCategories.FirstOrDefaultAsync(c => c.ExpenseCategoryId == categoryId.Value);
            if (existing == null) return new ExpenseResult(false, "البند غير موجود.");
            category = existing;
        }
        else
        {
            category = new ExpenseCategory();
            _db.ExpenseCategories.Add(category);
        }

        category.Name = name.Trim();
        category.SortOrder = sortOrder;
        category.IsActive = isActive;
        category.MaxAmount = maxAmount;

        await _db.SaveChangesAsync();
        return new ExpenseResult(true, categoryId.HasValue ? "تم تحديث البند." : "تمت إضافة البند.");
    }

    /// <summary>Deletes a category, but only when no expense references it (else deactivate it).</summary>
    public async Task<ExpenseResult> DeleteCategoryAsync(int categoryId)
    {
        var category = await _db.ExpenseCategories.FirstOrDefaultAsync(c => c.ExpenseCategoryId == categoryId);
        if (category == null) return new ExpenseResult(false, "البند غير موجود.");

        var inUse = await _db.Expenses.AnyAsync(e => e.ExpenseCategoryId == categoryId);
        if (inUse)
            return new ExpenseResult(false, "لا يمكن حذف بند مرتبط بمصروفات؛ عطّله بدلاً من ذلك.");

        _db.ExpenseCategories.Remove(category);
        await _db.SaveChangesAsync();
        return new ExpenseResult(true, "تم حذف البند.");
    }
}
