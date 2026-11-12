using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Appends to the operation log. The row's hash-chain link is filled in by
/// <see cref="AuditChainSaveChangesInterceptor"/> on save, and the table itself rejects
/// UPDATE/DELETE, so the log is append-only and tamper-evident.
/// </summary>
public class AuditLogService
{
    private readonly ECertDbContext _db;
    public AuditLogService(ECertDbContext db) => _db = db;

    public async Task LogAsync(string userName, string action, string entityType, int? entityId = null, string? oldValues = null, string? newValues = null, string? ip = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            UserName = userName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldValues = oldValues,
            NewValues = newValues,
            Timestamp = DateTime.Now,
            IpAddress = ip
        });
        await _db.SaveChangesAsync();
    }
}
