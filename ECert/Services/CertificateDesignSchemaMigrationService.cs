using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Performs an additive schema upgrade. Existing certificate settings and issued certificates are
/// never modified; the design layer can therefore be deployed safely on an existing database.
/// </summary>
public sealed class CertificateDesignSchemaMigrationService
{
    private readonly ECertDbContext _db;

    public CertificateDesignSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `CertificateDesigns` (
    `CertificateDesignId` INT NOT NULL AUTO_INCREMENT,
    `Name` VARCHAR(120) NOT NULL,
    `DesignKey` VARCHAR(80) NOT NULL,
    `IsPublished` TINYINT(1) NOT NULL DEFAULT 0,
    `CanvasWidth` INT NOT NULL DEFAULT 1120,
    `CanvasHeight` INT NOT NULL DEFAULT 792,
    `BackgroundColor` VARCHAR(7) NOT NULL DEFAULT '#fffdf7',
    `BorderColor` VARCHAR(7) NOT NULL DEFAULT '#c9a227',
    `BorderWidth` INT NOT NULL DEFAULT 12,
    `BorderRadius` INT NOT NULL DEFAULT 8,
    `UpdatedBy` VARCHAR(100) NULL,
    `CreatedAt` DATETIME NOT NULL,
    `UpdatedAt` DATETIME NOT NULL,
    PRIMARY KEY (`CertificateDesignId`),
    UNIQUE KEY `IX_CertificateDesigns_DesignKey` (`DesignKey`)
) CHARACTER SET utf8mb4;");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `CertificateDesignElements` (
    `CertificateDesignElementId` INT NOT NULL AUTO_INCREMENT,
    `CertificateDesignId` INT NOT NULL,
    `ElementType` VARCHAR(20) NOT NULL,
    `FieldKey` VARCHAR(50) NOT NULL,
    `Content` VARCHAR(1000) NOT NULL,
    `X` INT NOT NULL,
    `Y` INT NOT NULL,
    `Width` INT NOT NULL,
    `Height` INT NOT NULL,
    `FontSize` INT NOT NULL,
    `FontFamily` VARCHAR(40) NOT NULL,
    `FontColor` VARCHAR(7) NOT NULL,
    `FontWeight` VARCHAR(20) NOT NULL,
    `TextAlign` VARCHAR(10) NOT NULL,
    `IsVisible` TINYINT(1) NOT NULL DEFAULT 1,
    `ZIndex` INT NOT NULL DEFAULT 1,
    `SortOrder` INT NOT NULL DEFAULT 0,
    PRIMARY KEY (`CertificateDesignElementId`),
    KEY `IX_CertificateDesignElements_DesignId` (`CertificateDesignId`),
    CONSTRAINT `FK_CertificateDesignElements_CertificateDesigns`
        FOREIGN KEY (`CertificateDesignId`) REFERENCES `CertificateDesigns` (`CertificateDesignId`)
        ON DELETE CASCADE
) CHARACTER SET utf8mb4;");

        var rotationColumnExists = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS `Value` FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'CertificateDesignElements' AND COLUMN_NAME = 'Rotation'")
            .SingleAsync() > 0;
        if (!rotationColumnExists)
        {
            await _db.Database.ExecuteSqlRawAsync("ALTER TABLE `CertificateDesignElements` ADD COLUMN `Rotation` INT NOT NULL DEFAULT 0");
        }

        var backgroundImageColumnExists = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS `Value` FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'CertificateDesigns' AND COLUMN_NAME = 'BackgroundImageUrl'")
            .SingleAsync() > 0;
        if (!backgroundImageColumnExists)
        {
            await _db.Database.ExecuteSqlRawAsync("ALTER TABLE `CertificateDesigns` ADD COLUMN `BackgroundImageUrl` VARCHAR(400) NULL");
        }

        await EnsureColumnAsync("Instructors", "SignatureImageUrl", "VARCHAR(400) NULL");

        var courseDesignColumnExists = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS `Value` FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Courses' AND COLUMN_NAME = 'CertificateDesignId'")
            .SingleAsync() > 0;
        if (!courseDesignColumnExists)
        {
            await _db.Database.ExecuteSqlRawAsync("ALTER TABLE `Courses` ADD COLUMN `CertificateDesignId` INT NULL");
        }

        var courseDesignIndexExists = await _db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS `Value` FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'Courses' AND INDEX_NAME = 'IX_Courses_CertificateDesignId'")
            .SingleAsync() > 0;
        if (!courseDesignIndexExists)
        {
            await _db.Database.ExecuteSqlRawAsync("CREATE INDEX `IX_Courses_CertificateDesignId` ON `Courses` (`CertificateDesignId`)");
        }

        if (!await _db.CertificateDesigns.AnyAsync())
        {
            _db.CertificateDesigns.Add(CertificateDesignService.CreateDefault("system"));
            await _db.SaveChangesAsync();
        }

        var published = await _db.CertificateDesigns
            .Where(t => t.IsPublished)
            .OrderByDescending(t => t.UpdatedAt)
            .ThenByDescending(t => t.CertificateDesignId)
            .ToListAsync();

        if (published.Count > 1)
        {
            foreach (var duplicate in published.Skip(1))
                duplicate.IsPublished = false;

            await _db.SaveChangesAsync();
        }
    }

    private async Task EnsureColumnAsync(string tableName, string columnName, string definitionSql)
    {
        if (await ColumnExistsAsync(tableName, columnName)) return;
        await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definitionSql}");
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (shouldClose) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND COLUMN_NAME = @columnName;";
            var tableParameter = command.CreateParameter();
            tableParameter.ParameterName = "@tableName";
            tableParameter.Value = tableName;
            command.Parameters.Add(tableParameter);
            var columnParameter = command.CreateParameter();
            columnParameter.ParameterName = "@columnName";
            columnParameter.Value = columnName;
            command.Parameters.Add(columnParameter);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            if (shouldClose && connection.State == System.Data.ConnectionState.Open)
                await connection.CloseAsync();
        }
    }
}
