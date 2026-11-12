using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Owns installment plans (خطط الأقساط). A plan only describes a schedule of due dates; the balance
/// is never stored on the plan. Instead <see cref="Allocate"/> distributes the invoice's paid amount
/// over the installments in due-date order, so an installment is "paid" exactly when the money
/// received has reached it — the schedule can never drift from the ledger.
/// </summary>
public class InstallmentService
{
    private readonly ECertDbContext _db;
    public InstallmentService(ECertDbContext db) => _db = db;

    public sealed record InstallmentView(int Sequence, decimal Amount, DateTime DueDate, decimal Paid, decimal Remaining, string Status, int DaysOverdue);

    /// <summary>A plan together with its derived installment views, for the plans listing.</summary>
    public sealed record PlanRow(InstallmentPlan Plan, List<InstallmentView> Views, int PaidCount, int LateCount, decimal Remaining);

    /// <summary>A plan and only the installments currently overdue, for the late-installments page.</summary>
    public sealed record LatePlanRow(InstallmentPlan Plan, List<InstallmentView> LateViews);

    public sealed record PlanResult(bool Success, string Message, InstallmentPlan? Plan = null);

    /// <summary>Builds a plan from explicit (amount, dueDate) rows. Replaces any existing plan for the invoice.</summary>
    public async Task<PlanResult> CreateAsync(Invoice invoice, IEnumerable<(decimal Amount, DateTime DueDate)> rows, string? note, string actor)
    {
        var items = rows.Where(r => r.Amount > 0).OrderBy(r => r.DueDate).ToList();
        if (items.Count == 0)
            return new PlanResult(false, "يجب إضافة قسط واحد على الأقل بمبلغ أكبر من صفر.");

        var total = items.Sum(i => i.Amount);
        if (total != Math.Round(invoice.TotalAmount, 2))
            return new PlanResult(false, $"مجموع الأقساط ({total:N2}) يجب أن يساوي إجمالي الفاتورة ({invoice.TotalAmount:N2}).");

        // A single plan per invoice keeps the schedule unambiguous.
        var existing = await _db.InstallmentPlans.Where(p => p.InvoiceId == invoice.InvoiceId).ToListAsync();
        if (existing.Count > 0)
            _db.InstallmentPlans.RemoveRange(existing);

        var plan = new InstallmentPlan
        {
            InvoiceId = invoice.InvoiceId,
            RegistrationId = invoice.RegistrationId,
            TotalAmount = total,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedBy = actor,
            CreatedAt = DateTime.Now
        };
        var sequence = 1;
        foreach (var (amount, dueDate) in items)
        {
            plan.Installments.Add(new Installment
            {
                Sequence = sequence++,
                Amount = Math.Round(amount, 2),
                DueDate = dueDate.Date
            });
        }
        _db.InstallmentPlans.Add(plan);
        await _db.SaveChangesAsync();
        return new PlanResult(true, "تم حفظ خطة الأقساط.", plan);
    }

    /// <summary>Distributes <paramref name="paid"/> over the installments in due-date order.</summary>
    public static List<InstallmentView> Allocate(IEnumerable<Installment> installments, decimal paid, DateTime today)
    {
        var ordered = installments.OrderBy(i => i.DueDate).ThenBy(i => i.Sequence).ToList();
        var remainingPaid = paid;
        var result = new List<InstallmentView>();
        foreach (var inst in ordered)
        {
            var applied = Math.Min(remainingPaid, inst.Amount);
            remainingPaid -= applied;
            var remaining = inst.Amount - applied;
            var status = remaining <= 0 ? "Paid"
                : applied > 0 ? "Partial"
                : inst.DueDate.Date < today.Date ? "Late"
                : "Upcoming";
            var daysOverdue = status == "Late" ? (today.Date - inst.DueDate.Date).Days : 0;
            result.Add(new InstallmentView(inst.Sequence, inst.Amount, inst.DueDate, applied, remaining, status, daysOverdue));
        }
        return result;
    }

    /// <summary>Plans whose next unpaid installment is already past its due date.</summary>
    public async Task<List<Installment>> LateInstallmentsAsync(System.Security.Claims.ClaimsPrincipal user, DateTime today)
    {
        var plans = await _db.InstallmentPlans
            .Include(p => p.Installments)
            .Include(p => p.Invoice).ThenInclude(i => i!.Registration).ThenInclude(r => r!.Course)
            .InScope(user)
            .ToListAsync();

        var late = new List<Installment>();
        foreach (var plan in plans)
        {
            var paid = await _db.Payments
                .Where(p => p.InvoiceId == plan.InvoiceId && !p.IsCancelled)
                .SumAsync(p => (decimal?)p.Amount) ?? 0m;
            var refunded = await _db.Refunds
                .Where(r => r.InvoiceId == plan.InvoiceId && r.Status == RefundStatus.Approved)
                .SumAsync(r => (decimal?)r.Amount) ?? 0m;
            var netPaid = paid - refunded;
            var views = Allocate(plan.Installments, netPaid, today);
            foreach (var view in views.Where(v => v.Status == "Late"))
            {
                var entity = plan.Installments.First(i => i.Sequence == view.Sequence);
                late.Add(entity);
            }
        }
        return late;
    }
}
