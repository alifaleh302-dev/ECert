using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>ينشئ جدول الصندوق (التوريدات) عبر سكربت CREATE IF NOT EXISTS متوافق مع MySQL.
/// لا يتم الاعتماد على EF EnsureCreated كلياً لأن EF قد لا يلتقط الجداول الجديدة فوراً.</summary>
public class CashboxSchemaMigrationService
{
    private readonly ECertDbContext _db;
    public CashboxSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `CashboxTransfers` (
    `Id` INT NOT NULL AUTO_INCREMENT,
    `CourseId` INT NULL,
    `Amount` DECIMAL(18,2) NOT NULL,
    `Note` VARCHAR(500) NULL,
    `CreatedBy` VARCHAR(150) NOT NULL,
    `CreatedAt` DATETIME(6) NOT NULL,
    PRIMARY KEY (`Id`),
    KEY `IX_CashboxTransfers_CourseId` (`CourseId`),
    CONSTRAINT `FK_CashboxTransfers_Courses_CourseId`
        FOREIGN KEY (`CourseId`) REFERENCES `Courses` (`CourseId`)
        ON DELETE SET NULL
) CHARACTER SET utf8mb4;");

        // The standalone "withdrawal from the fund" feature is retired: money now leaves the fund
        // only through an approved expense or a refund paid from the fund. Drop the legacy table so
        // no withdrawal-related state lingers. Idempotent, so it is safe on every startup.
        await _db.Database.ExecuteSqlRawAsync("DROP TABLE IF EXISTS `CashboxWithdrawals`;");
    }
}
