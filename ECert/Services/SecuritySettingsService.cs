using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Reads (and lazily creates) the single <see cref="SecuritySettings"/> row. The security services
/// — the login throttle, 2FA, alerting and the backup job — all resolve their configuration here,
/// so an administrator can change policy from <c>/SecuritySettings</c> without a redeploy. Every
/// read also refreshes <see cref="SecurityPolicySnapshot"/>, which the request pipeline consults on
/// the hot path.
/// </summary>
public class SecuritySettingsService
{
    private readonly ECertDbContext _db;
    public SecuritySettingsService(ECertDbContext db) => _db = db;

    /// <summary>The current settings, creating the default row on first access.</summary>
    public async Task<SecuritySettings> GetAsync()
    {
        var settings = await _db.SecuritySettings
            .FirstOrDefaultAsync(s => s.SecuritySettingsId == SecuritySettings.SingletonId);

        if (settings == null)
        {
            settings = new SecuritySettings { SecuritySettingsId = SecuritySettings.SingletonId };
            _db.SecuritySettings.Add(settings);
            await _db.SaveChangesAsync();
        }

        SecurityPolicySnapshot.Update(settings.MaxFailedLoginAttempts, settings.LockoutMinutes,
            settings.IdleTimeoutMinutes, settings.TwoFactorEnabled);

        return settings;
    }

    /// <summary>Persists edits and refreshes the runtime policy snapshot.</summary>
    public async Task SaveAsync(SecuritySettings settings, string actor)
    {
        settings.UpdatedAt = DateTime.Now;
        settings.UpdatedBy = actor;
        _db.SecuritySettings.Update(settings);
        await _db.SaveChangesAsync();
        SecurityPolicySnapshot.Update(settings.MaxFailedLoginAttempts, settings.LockoutMinutes,
            settings.IdleTimeoutMinutes, settings.TwoFactorEnabled);
    }
}
