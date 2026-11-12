using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Owns the accounting-period lock: the last date closed to financial edits. Every money path asks
/// this service before it writes, so a closed period cannot be changed by a payment, a refund, a
/// handover or a cashbox movement — the rule lives in one place instead of being re-implemented per
/// controller. Locking and reopening are both recorded on the append-only audit trail by the caller.
/// </summary>
public class PeriodLockService
{
    private readonly ECertDbContext _db;
    public PeriodLockService(ECertDbContext db) => _db = db;

    /// <summary>The last day closed to edits, or null when no period is locked.</summary>
    public async Task<DateTime?> GetLockedThroughAsync() =>
        await _db.AccountingPeriodLocks.Where(l => l.IsActive)
            .OrderByDescending(l => l.LockedThrough)
            .Select(l => (DateTime?)l.LockedThrough)
            .FirstOrDefaultAsync();

    /// <summary>True when the given day falls inside a locked (closed) period.</summary>
    public async Task<bool> IsLockedAsync(DateTime date)
    {
        var lockedThrough = await GetLockedThroughAsync();
        return lockedThrough.HasValue && date.Date <= lockedThrough.Value.Date;
    }

    /// <summary>Arabic message for a blocked write, or null when the day is open.</summary>
    public async Task<string?> BlockedMessageAsync(DateTime date)
    {
        var lockedThrough = await GetLockedThroughAsync();
        return lockedThrough.HasValue && date.Date <= lockedThrough.Value.Date
            ? $"الفترة المالية مقفلة حتى {lockedThrough.Value:yyyy-MM-dd}؛ لا يمكن تعديل حركة تقع ضمنها."
            : null;
    }

    /// <summary>Closes the period up to and including <paramref name="through"/>, replacing any prior lock.</summary>
    public async Task LockAsync(DateTime through, string? note, string actor)
    {
        await DeactivateAllAsync();
        _db.AccountingPeriodLocks.Add(new AccountingPeriodLock
        {
            LockedThrough = through.Date,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            IsActive = true,
            LockedBy = actor,
            LockedAt = DateTime.Now
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>Reopens the currently locked period; the history of the lock is preserved.</summary>
    public async Task ReopenAsync(string actor, string? note)
    {
        var active = await _db.AccountingPeriodLocks.Where(l => l.IsActive).ToListAsync();
        foreach (var lockRow in active)
        {
            lockRow.IsActive = false;
            if (!string.IsNullOrWhiteSpace(note)) lockRow.Note = $"{lockRow.Note} | إعادة فتح: {note}".Trim();
        }
        await _db.SaveChangesAsync();
    }

    public Task<List<AccountingPeriodLock>> HistoryAsync(int take = 50) =>
        _db.AccountingPeriodLocks.OrderByDescending(l => l.LockedAt).ThenByDescending(l => l.AccountingPeriodLockId)
            .Take(take).ToListAsync();

    private async Task DeactivateAllAsync()
    {
        var active = await _db.AccountingPeriodLocks.Where(l => l.IsActive).ToListAsync();
        foreach (var lockRow in active) lockRow.IsActive = false;
        await _db.SaveChangesAsync();
    }
}
