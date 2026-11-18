using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Creates the tables the financial v3 release adds — expense categories/expenses and installment
/// plans/installments — using idempotent <c>CREATE TABLE IF NOT EXISTS</c> statements compatible with
/// MySQL and MariaDB, so the service can run on every startup without touching existing data.
///
/// Ordering note: it must run before any migration or the seeder that materialises an
/// <c>Expense</c>, <c>ExpenseCategory</c>, <c>InstallmentPlan</c> or <c>Installment</c> through EF
/// (EF selects every mapped column).
/// </summary>
public class ExpenseInstallmentSchemaMigrationService
{
    private readonly ECertDbContext _db;
    public ExpenseInstallmentSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `ExpenseCategories` (
    `ExpenseCategoryId` INT NOT NULL AUTO_INCREMENT,
    `Name` VARCHAR(120) NOT NULL,
    `IsActive` TINYINT(1) NOT NULL DEFAULT 1,
    `SortOrder` INT NOT NULL DEFAULT 0,
    `MaxAmount` DECIMAL(18,2) NULL,
    `CreatedAt` DATETIME(6) NOT NULL,
    PRIMARY KEY (`ExpenseCategoryId`)
) CHARACTER SET utf8mb4;");

        // An existing deployment predates the per-category budget cap. MySQL has no
        // `ADD COLUMN IF NOT EXISTS` (MariaDB does), so guard with an INFORMATION_SCHEMA check.
        await EnsureColumnAsync("ExpenseCategories", "MaxAmount", "DECIMAL(18,2) NULL");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `Expenses` (
    `ExpenseId` INT NOT NULL AUTO_INCREMENT,
    `ExpenseCategoryId` INT NOT NULL,
    `Description` VARCHAR(300) NOT NULL,
    `Amount` DECIMAL(18,2) NOT NULL,
    `ExpenseDate` DATETIME(6) NOT NULL,
    `DocumentNumber` VARCHAR(80) NULL,
    `AttachmentUrl` VARCHAR(400) NULL,
    `Status` VARCHAR(40) NOT NULL,
    `SubmittedBy` VARCHAR(150) NOT NULL,
    `SubmittedByUserId` INT NULL,
    `SubmittedAt` DATETIME(6) NOT NULL,
    `ReviewedBy` VARCHAR(150) NULL,
    `ReviewedAt` DATETIME(6) NULL,
    `ReviewNote` VARCHAR(500) NULL,
    PRIMARY KEY (`ExpenseId`),
    KEY `IX_Expenses_Category_Date` (`ExpenseCategoryId`, `ExpenseDate`),
    KEY `IX_Expenses_Status` (`Status`),
    KEY `IX_Expenses_SubmittedByUserId` (`SubmittedByUserId`),
    CONSTRAINT `FK_Expenses_ExpenseCategories_ExpenseCategoryId`
        FOREIGN KEY (`ExpenseCategoryId`) REFERENCES `ExpenseCategories` (`ExpenseCategoryId`)
        ON DELETE RESTRICT
) CHARACTER SET utf8mb4;");

        // An existing deployment predates the "submitter may withdraw their own request" column.
        await EnsureColumnAsync("Expenses", "SubmittedByUserId", "INT NULL");
        await EnsureIndexAsync("Expenses", "IX_Expenses_SubmittedByUserId", "`SubmittedByUserId`");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `ExpenseAttachments` (
    `ExpenseAttachmentId` INT NOT NULL AUTO_INCREMENT,
    `ExpenseId` INT NOT NULL,
    `Url` VARCHAR(400) NOT NULL,
    `SortOrder` INT NOT NULL DEFAULT 0,
    PRIMARY KEY (`ExpenseAttachmentId`),
    KEY `IX_ExpenseAttachments_ExpenseId` (`ExpenseId`),
    CONSTRAINT `FK_ExpenseAttachments_Expenses_ExpenseId`
        FOREIGN KEY (`ExpenseId`) REFERENCES `Expenses` (`ExpenseId`)
        ON DELETE CASCADE
) CHARACTER SET utf8mb4;");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `InstallmentPlans` (
    `InstallmentPlanId` INT NOT NULL AUTO_INCREMENT,
    `InvoiceId` INT NOT NULL,
    `RegistrationId` INT NOT NULL,
    `TotalAmount` DECIMAL(18,2) NOT NULL,
    `Note` VARCHAR(500) NULL,
    `CreatedBy` VARCHAR(150) NOT NULL,
    `CreatedAt` DATETIME(6) NOT NULL,
    PRIMARY KEY (`InstallmentPlanId`),
    KEY `IX_InstallmentPlans_InvoiceId` (`InvoiceId`),
    KEY `IX_InstallmentPlans_RegistrationId` (`RegistrationId`),
    CONSTRAINT `FK_InstallmentPlans_Invoices_InvoiceId`
        FOREIGN KEY (`InvoiceId`) REFERENCES `Invoices` (`InvoiceId`)
        ON DELETE CASCADE
) CHARACTER SET utf8mb4;");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `Installments` (
    `InstallmentId` INT NOT NULL AUTO_INCREMENT,
    `InstallmentPlanId` INT NOT NULL,
    `Sequence` INT NOT NULL,
    `Amount` DECIMAL(18,2) NOT NULL,
    `DueDate` DATETIME(6) NOT NULL,
    PRIMARY KEY (`InstallmentId`),
    KEY `IX_Installments_Plan_Due` (`InstallmentPlanId`, `DueDate`),
    CONSTRAINT `FK_Installments_InstallmentPlans_InstallmentPlanId`
        FOREIGN KEY (`InstallmentPlanId`) REFERENCES `InstallmentPlans` (`InstallmentPlanId`)
        ON DELETE CASCADE
) CHARACTER SET utf8mb4;");
    }

    /// <summary>
    /// Adds a column only when it is missing. Uses INFORMATION_SCHEMA rather than
    /// <c>ADD COLUMN IF NOT EXISTS</c>, which MySQL does not support (MariaDB does).
    /// </summary>
    private async Task EnsureColumnAsync(string tableName, string columnName, string definitionSql)
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
            var p1 = command.CreateParameter(); p1.ParameterName = "@tableName"; p1.Value = tableName; command.Parameters.Add(p1);
            var p2 = command.CreateParameter(); p2.ParameterName = "@columnName"; p2.Value = columnName; command.Parameters.Add(p2);
            var exists = Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
            if (exists) return;
        }
        finally
        {
            if (close && connection.State == System.Data.ConnectionState.Open)
                await connection.CloseAsync();
        }

        await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE `{tableName}` ADD COLUMN `{columnName}` {definitionSql};");
    }

    /// <summary>
    /// Adds a secondary index only when it is missing, guarding with INFORMATION_SCHEMA.STATISTICS
    /// so a database upgraded from an older release (where the column may already exist) does not
    /// fail on a duplicate key name.
    /// </summary>
    private async Task EnsureIndexAsync(string tableName, string indexName, string columnsSql)
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
            var p1 = command.CreateParameter(); p1.ParameterName = "@tableName"; p1.Value = tableName; command.Parameters.Add(p1);
            var p2 = command.CreateParameter(); p2.ParameterName = "@indexName"; p2.Value = indexName; command.Parameters.Add(p2);
            var exists = Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
            if (exists) return;
        }
        finally
        {
            if (close && connection.State == System.Data.ConnectionState.Open)
                await connection.CloseAsync();
        }

        await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE `{tableName}` ADD INDEX `{indexName}` ({columnsSql});");
    }
}
