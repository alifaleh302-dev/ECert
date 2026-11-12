using ECert.Data;

namespace ECert.Services;

/// <summary>
/// Runs the encrypted backup once a day. The hour is configurable in <c>/SecuritySettings</c> and
/// re-read each cycle, so a schedule change takes effect the next day without a restart. The job
/// is disabled when backups are turned off or the backup key is not set.
/// </summary>
public class EncryptedBackupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EncryptedBackupService> _logger;

    public EncryptedBackupService(IServiceScopeFactory scopeFactory, ILogger<EncryptedBackupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small initial delay so the first cycle never races application startup.
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var (enabled, hour) = await ReadScheduleAsync(stoppingToken);
            var now = DateTime.UtcNow;
            var next = new DateTime(now.Year, now.Month, now.Day, hour, 0, 0, DateTimeKind.Utc);
            if (next <= now) next = next.AddDays(1);

            try
            {
                await Task.Delay(next - now, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!enabled) continue;

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var backup = scope.ServiceProvider.GetRequiredService<DatabaseBackupService>();
                var result = await backup.RunAsync(stoppingToken);
                _logger.LogInformation("Scheduled backup finished: {Result}", result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled backup failed.");
            }
        }
    }

    private async Task<(bool Enabled, int Hour)> ReadScheduleAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ECertDbContext>();
            var settings = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions
                .FirstOrDefaultAsync(db.SecuritySettings, s => s.SecuritySettingsId == Models.SecuritySettings.SingletonId, cancellationToken);
            return (settings?.BackupEnabled ?? false, Math.Clamp(settings?.BackupHourUtc ?? 2, 0, 23));
        }
        catch
        {
            return (false, 2);
        }
    }
}
