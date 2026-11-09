using System.Data;
using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Creates the <c>CoursePartners</c> table and migrates the legacy single-partner columns
/// (<c>Courses.PartnerName</c>/<c>PartnerLogoUrl</c>) into it, so older courses keep their logo.
/// </summary>
public sealed class CoursePartnerSchemaMigrationService
{
    private readonly ECertDbContext _db;

    public CoursePartnerSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `CoursePartners` (
    `CoursePartnerId` INT NOT NULL AUTO_INCREMENT,
    `CourseId` INT NOT NULL,
    `Name` VARCHAR(150) NOT NULL,
    `LogoUrl` VARCHAR(400) NULL,
    `SignatureUrl` VARCHAR(400) NULL,
    `SortOrder` INT NOT NULL DEFAULT 0,
    PRIMARY KEY (`CoursePartnerId`),
    KEY `IX_CoursePartners_CourseId_SortOrder` (`CourseId`, `SortOrder`),
    CONSTRAINT `FK_CoursePartners_Courses_CourseId`
        FOREIGN KEY (`CourseId`) REFERENCES `Courses` (`CourseId`) ON DELETE CASCADE
) ENGINE=InnoDB CHARACTER SET utf8mb4;");

        // A deployment that already ran the first cut of this table predates the per-partner
        // signature, so add the column when it is missing.
        if (await TableExistsAsync("CoursePartners") && !await ColumnExistsAsync("CoursePartners", "SignatureUrl"))
            await _db.Database.ExecuteSqlRawAsync("ALTER TABLE `CoursePartners` ADD COLUMN `SignatureUrl` VARCHAR(400) NULL;");

        await MigrateLegacyPartnersAsync();
    }

    /// <summary>
    /// Copies each course's legacy partner into the new table exactly once. Guarded by the
    /// absence of any existing row for that course, so repeated startups never duplicate it.
    /// </summary>
    private async Task MigrateLegacyPartnersAsync()
    {
        if (!await TableExistsAsync("Courses"))
            return;

        await _db.Database.ExecuteSqlRawAsync(@"
INSERT INTO `CoursePartners` (`CourseId`, `Name`, `LogoUrl`, `SortOrder`)
SELECT c.`CourseId`,
       COALESCE(NULLIF(TRIM(c.`PartnerName`), ''), 'جهة داعمة'),
       NULLIF(TRIM(c.`PartnerLogoUrl`), ''),
       0
FROM `Courses` c
LEFT JOIN `CoursePartners` cp ON cp.`CourseId` = c.`CourseId`
WHERE cp.`CoursePartnerId` IS NULL
  AND (NULLIF(TRIM(c.`PartnerName`), '') IS NOT NULL
       OR NULLIF(TRIM(c.`PartnerLogoUrl`), '') IS NOT NULL);");
    }

    private async Task<bool> TableExistsAsync(string tableName)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != ConnectionState.Open;
        try
        {
            if (shouldCloseConnection)
                await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.TABLES
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = @tableName;";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@tableName";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);

            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            if (shouldCloseConnection && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldCloseConnection = connection.State != ConnectionState.Open;
        try
        {
            if (shouldCloseConnection)
                await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*)
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE()
  AND TABLE_NAME = @tableName
  AND COLUMN_NAME = @columnName;";

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
            if (shouldCloseConnection && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }
}
