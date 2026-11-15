using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Creates the <c>CertificateExportSettings</c> table and seeds its singleton row with safe
/// defaults. The migration is additive: no existing table is touched, so it is safe to deploy on
/// a live database.
/// </summary>
public sealed class CertificateExportSettingsSchemaMigrationService
{
    private readonly ECertDbContext _db;

    public CertificateExportSettingsSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `CertificateExportSettings` (
    `CertificateExportSettingsId` INT NOT NULL,
    `AllowPublicDownload` TINYINT(1) NOT NULL DEFAULT 0,
    `MaxCertificatesPerExport` INT NOT NULL DEFAULT 100,
    `PageSize` INT NOT NULL DEFAULT 0,
    `IncludeBleedMarks` TINYINT(1) NOT NULL DEFAULT 0,
    `FileNamePrefix` VARCHAR(40) NULL,
    `UpdatedAt` DATETIME NOT NULL,
    `UpdatedBy` VARCHAR(150) NULL,
    PRIMARY KEY (`CertificateExportSettingsId`)
) CHARACTER SET utf8mb4;");

        // Seed the single row so the settings page always has something to edit. The values here
        // must mirror the model defaults: public download off, 100 certificates per request.
        await _db.Database.ExecuteSqlRawAsync(@"
INSERT INTO `CertificateExportSettings`
    (`CertificateExportSettingsId`, `AllowPublicDownload`, `MaxCertificatesPerExport`,
     `PageSize`, `IncludeBleedMarks`, `UpdatedAt`)
SELECT 1, 0, 100, 0, 0, NOW()
WHERE NOT EXISTS (SELECT 1 FROM `CertificateExportSettings` WHERE `CertificateExportSettingsId` = 1);");
    }
}
