using ECert.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ECert.Services;

/// <summary>
/// Computes the audit hash chain as rows are written, for both audit tables. Doing it here (rather
/// than in each caller) means every insert is chained no matter which code path — or which batch —
/// produced it, and several rows added in one transaction are linked to each other in insertion
/// order. Rows that already carry a hash (the one-time backfill) are left untouched.
/// </summary>
public sealed class AuditChainSaveChangesInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void Apply(DbContext? context)
    {
        if (context is null) return;

        var logs = context.ChangeTracker.Entries<AuditLog>()
            .Where(e => e.State == EntityState.Added && string.IsNullOrEmpty(e.Entity.RowHash))
            .Select(e => e.Entity)
            .ToList();
        if (logs.Count > 0)
        {
            var previous = LastHash(context, context.Set<AuditLog>().OrderByDescending(l => l.AuditLogId).Select(l => l.RowHash));
            foreach (var log in logs)
            {
                log.Timestamp = AuditHashChain.Normalize(log.Timestamp);
                log.PreviousHash = previous;
                log.RowHash = AuditHashChain.Compute(log, previous);
                previous = log.RowHash;
            }
        }

        var audits = context.ChangeTracker.Entries<FinancialAuditEntry>()
            .Where(e => e.State == EntityState.Added && string.IsNullOrEmpty(e.Entity.RowHash))
            .Select(e => e.Entity)
            .ToList();
        if (audits.Count > 0)
        {
            var previous = LastHash(context, context.Set<FinancialAuditEntry>()
                .OrderByDescending(a => a.FinancialAuditEntryId).Select(a => a.RowHash));
            foreach (var audit in audits)
            {
                audit.Timestamp = AuditHashChain.Normalize(audit.Timestamp);
                audit.PreviousHash = previous;
                audit.RowHash = AuditHashChain.Compute(audit, previous);
                previous = audit.RowHash;
            }
        }
    }

    private static string LastHash(DbContext context, IQueryable<string?> query) =>
        query.FirstOrDefault() ?? string.Empty;
}
