using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Creates the tables and columns the financial v2 release adds, using idempotent
/// <c>CREATE TABLE IF NOT EXISTS</c> / guarded <c>ALTER TABLE</c> statements compatible with
/// MySQL and MariaDB. New columns are added only when missing, so the service can run on every
/// startup without touching existing data.
///
/// Ordering note: a migration that materialises an entity through EF selects <em>every</em> mapped
/// column, so this service must run before any migration (or the seeder) that loads a
/// <c>Payment</c>, <c>Refund</c>, <c>CustodyEntry</c> or <c>FinancialAuditEntry</c>.
/// </summary>
public class FinancialSchemaMigrationService
{
    private readonly ECertDbContext _db;
    public FinancialSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `CustodyEntries` (
    `CustodyEntryId` INT NOT NULL AUTO_INCREMENT,
    `UserId` INT NOT NULL,
    `Amount` DECIMAL(18,2) NOT NULL,
    `EntryType` VARCHAR(40) NOT NULL,
    `ReferenceType` VARCHAR(40) NULL,
    `ReferenceId` INT NULL,
    `ReferenceNumber` VARCHAR(80) NULL,
    `Note` VARCHAR(500) NULL,
    `CreatedBy` VARCHAR(150) NOT NULL,
    `CreatedAt` DATETIME(6) NOT NULL,
    PRIMARY KEY (`CustodyEntryId`),
    KEY `IX_CustodyEntries_UserId_CreatedAt` (`UserId`, `CreatedAt`),
    KEY `IX_CustodyEntries_Reference` (`ReferenceType`, `ReferenceId`),
    CONSTRAINT `FK_CustodyEntries_Users_UserId`
        FOREIGN KEY (`UserId`) REFERENCES `Users` (`UserId`)
        ON DELETE RESTRICT
) CHARACTER SET utf8mb4;");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `CustodyHandovers` (
    `CustodyHandoverId` INT NOT NULL AUTO_INCREMENT,
    `UserId` INT NOT NULL,
    `Amount` DECIMAL(18,2) NOT NULL,
    `Status` VARCHAR(40) NOT NULL,
    `Note` VARCHAR(500) NULL,
    `RequestedBy` VARCHAR(150) NOT NULL,
    `RequestedAt` DATETIME(6) NOT NULL,
    `ReviewedBy` VARCHAR(150) NULL,
    `ReviewedAt` DATETIME(6) NULL,
    `ReviewNote` VARCHAR(500) NULL,
    PRIMARY KEY (`CustodyHandoverId`),
    KEY `IX_CustodyHandovers_Status_RequestedAt` (`Status`, `RequestedAt`),
    KEY `IX_CustodyHandovers_UserId` (`UserId`),
    CONSTRAINT `FK_CustodyHandovers_Users_UserId`
        FOREIGN KEY (`UserId`) REFERENCES `Users` (`UserId`)
        ON DELETE RESTRICT
) CHARACTER SET utf8mb4;");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `Refunds` (
    `RefundId` INT NOT NULL AUTO_INCREMENT,
    `InvoiceId` INT NOT NULL,
    `RegistrationId` INT NOT NULL,
    `Amount` DECIMAL(18,2) NOT NULL,
    `Reason` VARCHAR(500) NOT NULL,
    `RefundMethod` VARCHAR(40) NOT NULL,
    `ReceiptNumber` VARCHAR(80) NULL,
    `RefundDate` DATETIME(6) NOT NULL,
    `RefundedBy` VARCHAR(150) NOT NULL,
    `PublicId` VARCHAR(64) NULL,
    `CreatedAt` DATETIME(6) NOT NULL,
    PRIMARY KEY (`RefundId`),
    UNIQUE KEY `IX_Refunds_PublicId` (`PublicId`),
    KEY `IX_Refunds_InvoiceId` (`InvoiceId`),
    KEY `IX_Refunds_RegistrationId` (`RegistrationId`),
    CONSTRAINT `FK_Refunds_Invoices_InvoiceId`
        FOREIGN KEY (`InvoiceId`) REFERENCES `Invoices` (`InvoiceId`)
        ON DELETE RESTRICT
) CHARACTER SET utf8mb4;");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `FinancialAuditEntries` (
    `FinancialAuditEntryId` INT NOT NULL AUTO_INCREMENT,
    `UserName` VARCHAR(150) NOT NULL,
    `ActionType` VARCHAR(40) NOT NULL,
    `Amount` DECIMAL(18,2) NOT NULL,
    `ReferenceType` VARCHAR(40) NULL,
    `ReferenceId` INT NULL,
    `ReferenceNumber` VARCHAR(80) NULL,
    `Details` VARCHAR(1000) NULL,
    `Timestamp` DATETIME(6) NOT NULL,
    PRIMARY KEY (`FinancialAuditEntryId`),
    KEY `IX_FinancialAuditEntries_Timestamp` (`Timestamp`),
    KEY `IX_FinancialAuditEntries_ActionType` (`ActionType`)
) CHARACTER SET utf8mb4;");

        await EnsureColumnAsync("Payments", "PublicId", "VARCHAR(64) NULL");
        await EnsureIndexAsync("Payments", "IX_Payments_PublicId", "`PublicId`", unique: true);
        await EnsureColumnAsync("Refunds", "PublicId", "VARCHAR(64) NULL");
        await EnsureIndexAsync("Refunds", "IX_Refunds_PublicId", "`PublicId`", unique: true);
    }

    private async Task EnsureColumnAsync(string tableName, string columnName, string definitionSql)
    {
        if (await ColumnExistsAsync(tableName, columnName)) return;
        await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definitionSql};");
    }

    private async Task EnsureIndexAsync(string tableName, string indexName, string columns, bool unique)
    {
        if (await IndexExistsAsync(tableName, indexName)) return;
        var uniqueKeyword = unique ? "UNIQUE " : string.Empty;
        await _db.Database.ExecuteSqlRawAsync($"CREATE {uniqueKeyword}INDEX `{indexName}` ON `{tableName}` ({columns});");
    }

    private async Task<bool> ColumnExistsAsync(string tableName, string columnName)
    {
        var connection = _db.Database.GetDbConnection();
        var close = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (close) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND COLUMN_NAME = @columnName;";
            AddParameter(command, "@tableName", tableName);
            AddParameter(command, "@columnName", columnName);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            if (close && connection.State == System.Data.ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private async Task<bool> IndexExistsAsync(string tableName, string indexName)
    {
        var connection = _db.Database.GetDbConnection();
        var close = connection.State != System.Data.ConnectionState.Open;
        try
        {
            if (close) await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(*) FROM INFORMATION_SCHEMA.STATISTICS
WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @tableName AND INDEX_NAME = @indexName;";
            AddParameter(command, "@tableName", tableName);
            AddParameter(command, "@indexName", indexName);
            return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            if (close && connection.State == System.Data.ConnectionState.Open)
                await connection.CloseAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
