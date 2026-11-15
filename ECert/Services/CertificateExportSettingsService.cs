using ECert.Data;
using ECert.Models;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Reads (and lazily creates) the single <see cref="CertificateExportSettings"/> row. The PDF
/// export endpoints resolve their limits here, so a SuperAdmin can raise the per-request ceiling on
/// a larger host — or turn the public download off on a small one — without a redeploy.
/// </summary>
public class CertificateExportSettingsService
{
    private readonly ECertDbContext _db;
    public CertificateExportSettingsService(ECertDbContext db) => _db = db;

    /// <summary>The current settings, creating the default row on first access.</summary>
    public async Task<CertificateExportSettings> GetAsync()
    {
        var settings = await _db.CertificateExportSettings
            .FirstOrDefaultAsync(s => s.CertificateExportSettingsId == CertificateExportSettings.SingletonId);

        if (settings == null)
        {
            settings = new CertificateExportSettings
            {
                CertificateExportSettingsId = CertificateExportSettings.SingletonId
            };
            _db.CertificateExportSettings.Add(settings);
            await _db.SaveChangesAsync();
        }

        return settings;
    }

    /// <summary>Persists edits and stamps who changed them.</summary>
    public async Task SaveAsync(CertificateExportSettings settings, string actor)
    {
        settings.UpdatedAt = DateTime.Now;
        settings.UpdatedBy = actor;
        _db.CertificateExportSettings.Update(settings);
        await _db.SaveChangesAsync();
    }

    /// <summary>True when the public verification page may offer the certificate PDF.</summary>
    public async Task<bool> PublicDownloadAllowedAsync()
    {
        var settings = await GetAsync();
        return settings.AllowPublicDownload;
    }
}
