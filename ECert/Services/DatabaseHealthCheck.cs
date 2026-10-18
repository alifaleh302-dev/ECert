using ECert.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ECert.Services;

/// <summary>
/// Reports readiness by opening the MySQL connection. Used by the platform's health probe.
/// </summary>
public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly ECertDbContext _db;

    public DatabaseHealthCheck(ECertDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return HealthCheckResult.Healthy("قاعدة البيانات متصلة");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("تعذر الاتصال بقاعدة البيانات", ex);
        }
    }
}
