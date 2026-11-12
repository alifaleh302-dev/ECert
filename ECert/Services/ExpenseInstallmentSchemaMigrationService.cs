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
    `CreatedAt` DATETIME(6) NOT NULL,
    PRIMARY KEY (`ExpenseCategoryId`)
) CHARACTER SET utf8mb4;");

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
    `SubmittedAt` DATETIME(6) NOT NULL,
    `ReviewedBy` VARCHAR(150) NULL,
    `ReviewedAt` DATETIME(6) NULL,
    `ReviewNote` VARCHAR(500) NULL,
    PRIMARY KEY (`ExpenseId`),
    KEY `IX_Expenses_Category_Date` (`ExpenseCategoryId`, `ExpenseDate`),
    KEY `IX_Expenses_Status` (`Status`),
    CONSTRAINT `FK_Expenses_ExpenseCategories_ExpenseCategoryId`
        FOREIGN KEY (`ExpenseCategoryId`) REFERENCES `ExpenseCategories` (`ExpenseCategoryId`)
        ON DELETE RESTRICT
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
}
