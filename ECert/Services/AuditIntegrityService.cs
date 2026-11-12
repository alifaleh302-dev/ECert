using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>Outcome of a hash-chain verification pass.</summary>
public sealed record AuditIntegrityResult(bool Intact, int Checked, int? FirstBrokenId, string? Detail);

/// <summary>
/// Walks the audit hash chains and reports the first row whose stored hash does not match the hash
/// recomputed from its content and its predecessor. A mismatch means the row (or an earlier one)
/// was altered after the fact — the database triggers make that hard, and this check makes it
/// detectable even if the guard is bypassed.
/// </summary>
public class AuditIntegrityService
{
    private readonly ECertDbContext _db;
    public AuditIntegrityService(ECertDbContext db) => _db = db;

    public async Task<AuditIntegrityResult> VerifyOperationLogAsync()
    {
        var rows = await _db.AuditLogs.OrderBy(l => l.AuditLogId).ToListAsync();
        var previous = string.Empty;
        var checkedCount = 0;

        foreach (var row in rows)
        {
            checkedCount++;
            if (string.IsNullOrEmpty(row.RowHash))
                return new AuditIntegrityResult(false, checkedCount, row.AuditLogId, "سجل بدون توقيع (RowHash فارغ).");

            var expected = AuditHashChain.Compute(row, previous);
            if (!string.Equals(expected, row.RowHash, StringComparison.Ordinal))
                return new AuditIntegrityResult(false, checkedCount, row.AuditLogId, "توقيع السجل لا يطابق المحتوى — تم التلاعب.");

            if (!string.Equals(row.PreviousHash ?? string.Empty, previous, StringComparison.Ordinal))
                return new AuditIntegrityResult(false, checkedCount, row.AuditLogId, "سلسلة التوقيعات مقطوعة — حُذف سجل أو أُضيف خارج الترتيب.");

            previous = row.RowHash;
        }

        return new AuditIntegrityResult(true, checkedCount, null, null);
    }

    public async Task<AuditIntegrityResult> VerifyFinancialAuditAsync()
    {
        var rows = await _db.FinancialAuditEntries.OrderBy(e => e.FinancialAuditEntryId).ToListAsync();
        var previous = string.Empty;
        var checkedCount = 0;

        foreach (var row in rows)
        {
            checkedCount++;
            if (string.IsNullOrEmpty(row.RowHash))
                return new AuditIntegrityResult(false, checkedCount, row.FinancialAuditEntryId, "سجل بدون توقيع (RowHash فارغ).");

            var expected = AuditHashChain.Compute(row, previous);
            if (!string.Equals(expected, row.RowHash, StringComparison.Ordinal))
                return new AuditIntegrityResult(false, checkedCount, row.FinancialAuditEntryId, "توقيع السجل لا يطابق المحتوى — تم التلاعب.");

            if (!string.Equals(row.PreviousHash ?? string.Empty, previous, StringComparison.Ordinal))
                return new AuditIntegrityResult(false, checkedCount, row.FinancialAuditEntryId, "سلسلة التوقيعات مقطوعة — حُذف سجل أو أُضيف خارج الترتيب.");

            previous = row.RowHash;
        }

        return new AuditIntegrityResult(true, checkedCount, null, null);
    }
}
