using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Owns the expense lifecycle. An expense is submitted by a financial employee and must be approved
/// by a SuperAdmin before it counts in the reports; rejection changes nothing. Approval writes an
/// append-only audit row through the ledger so an expense is a first-class financial movement.
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

        if (await _periods.IsLockedAsync(expenseDate))
            return new ExpenseResult(false, (await _periods.BlockedMessageAsync(expenseDate))!);

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

        expense.Status = ExpenseStatus.Approved;
        expense.ReviewedBy = approver;
        expense.ReviewedAt = DateTime.Now;
        expense.ReviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        var category = await _db.ExpenseCategories.FirstOrDefaultAsync(c => c.ExpenseCategoryId == expense.ExpenseCategoryId);
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
}
