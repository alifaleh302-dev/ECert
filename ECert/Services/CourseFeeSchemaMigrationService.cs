using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Creates the fee tables (<c>CourseFees</c>, <c>RegistrationFees</c>, <c>InvoiceItems</c>) for
/// existing databases and backfills one "course fee" line for every invoice created before line
/// items existed. Additive only: nothing is dropped or rewritten.
/// </summary>
public sealed class CourseFeeSchemaMigrationService
{
    private readonly ECertDbContext _db;

    public CourseFeeSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `CourseFees` (
    `CourseFeeId` INT NOT NULL AUTO_INCREMENT,
    `CourseId` INT NOT NULL,
    `Name` VARCHAR(150) NOT NULL,
    `FeeType` VARCHAR(30) NOT NULL DEFAULT 'Certificate',
    `Description` VARCHAR(500) NULL,
    `Amount` DECIMAL(18,2) NOT NULL DEFAULT 0,
    `IsOptional` TINYINT(1) NOT NULL DEFAULT 1,
    `IsActive` TINYINT(1) NOT NULL DEFAULT 1,
    `SortOrder` INT NOT NULL DEFAULT 0,
    `CreatedAt` DATETIME NOT NULL,
    PRIMARY KEY (`CourseFeeId`),
    KEY `IX_CourseFees_CourseId_SortOrder` (`CourseId`, `SortOrder`),
    CONSTRAINT `FK_CourseFees_Courses_CourseId`
        FOREIGN KEY (`CourseId`) REFERENCES `Courses` (`CourseId`) ON DELETE CASCADE
) ENGINE=InnoDB CHARACTER SET utf8mb4;");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `RegistrationFees` (
    `RegistrationFeeId` INT NOT NULL AUTO_INCREMENT,
    `RegistrationId` INT NOT NULL,
    `CourseFeeId` INT NULL,
    `Name` VARCHAR(150) NOT NULL,
    `FeeType` VARCHAR(30) NOT NULL DEFAULT 'Certificate',
    `Description` VARCHAR(500) NULL,
    `Amount` DECIMAL(18,2) NOT NULL DEFAULT 0,
    `IsOptional` TINYINT(1) NOT NULL DEFAULT 0,
    `IsSelected` TINYINT(1) NOT NULL DEFAULT 0,
    `CreatedAt` DATETIME NOT NULL,
    PRIMARY KEY (`RegistrationFeeId`),
    KEY `IX_RegistrationFees_RegistrationId` (`RegistrationId`),
    KEY `IX_RegistrationFees_CourseFeeId` (`CourseFeeId`),
    CONSTRAINT `FK_RegistrationFees_Registrations_RegistrationId`
        FOREIGN KEY (`RegistrationId`) REFERENCES `Registrations` (`RegistrationId`) ON DELETE CASCADE,
    CONSTRAINT `FK_RegistrationFees_CourseFees_CourseFeeId`
        FOREIGN KEY (`CourseFeeId`) REFERENCES `CourseFees` (`CourseFeeId`) ON DELETE SET NULL
) ENGINE=InnoDB CHARACTER SET utf8mb4;");

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `InvoiceItems` (
    `InvoiceItemId` INT NOT NULL AUTO_INCREMENT,
    `InvoiceId` INT NOT NULL,
    `Kind` VARCHAR(20) NOT NULL DEFAULT 'Fee',
    `FeeType` VARCHAR(30) NULL,
    `Name` VARCHAR(150) NOT NULL,
    `Description` VARCHAR(500) NULL,
    `Amount` DECIMAL(18,2) NOT NULL DEFAULT 0,
    `IsOptional` TINYINT(1) NOT NULL DEFAULT 0,
    `SortOrder` INT NOT NULL DEFAULT 0,
    `CreatedBy` VARCHAR(150) NULL,
    `CreatedAt` DATETIME NOT NULL,
    PRIMARY KEY (`InvoiceItemId`),
    KEY `IX_InvoiceItems_InvoiceId_SortOrder` (`InvoiceId`, `SortOrder`),
    CONSTRAINT `FK_InvoiceItems_Invoices_InvoiceId`
        FOREIGN KEY (`InvoiceId`) REFERENCES `Invoices` (`InvoiceId`) ON DELETE CASCADE
) ENGINE=InnoDB CHARACTER SET utf8mb4;");

        await BackfillInvoiceItemsAsync();
    }

    /// <summary>
    /// Every pre-existing invoice gets one course line equal to its original amount, so the new
    /// totals and the old totals agree. Guarded by "no items yet" so restarts never duplicate it.
    /// </summary>
    private async Task BackfillInvoiceItemsAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
INSERT INTO `InvoiceItems` (`InvoiceId`, `Kind`, `FeeType`, `Name`, `Description`, `Amount`, `IsOptional`, `SortOrder`, `CreatedBy`, `CreatedAt`)
SELECT i.`InvoiceId`,
       'Course',
       NULL,
       'رسوم الدورة',
       NULL,
       CASE WHEN i.`OriginalAmount` > 0 THEN i.`OriginalAmount` ELSE i.`TotalAmount` END,
       0,
       0,
       i.`CreatedBy`,
       i.`CreatedAt`
FROM `Invoices` i
LEFT JOIN `InvoiceItems` ii ON ii.`InvoiceId` = i.`InvoiceId`
WHERE ii.`InvoiceItemId` IS NULL;");
    }
}
