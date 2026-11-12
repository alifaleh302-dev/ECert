using System.Data;
using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Creates the <c>UserCourses</c> table that confines an administrative user to a set of courses.
/// Databases created before this feature lack the table; <c>EnsureCreated</c> never alters an
/// existing database, so it is created here. Must run before <c>DbSeeder.Seed</c>, which loads
/// Users (and their course assignments) through EF.
/// </summary>
public sealed class UserCourseSchemaMigrationService
{
    private readonly ECertDbContext _db;

    public UserCourseSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        if (!await TableExistsAsync("Users") || !await TableExistsAsync("Courses"))
            return;

        await _db.Database.ExecuteSqlRawAsync(@"
CREATE TABLE IF NOT EXISTS `UserCourses` (
    `UserCourseId` INT NOT NULL AUTO_INCREMENT,
    `UserId` INT NOT NULL,
    `CourseId` INT NOT NULL,
    PRIMARY KEY (`UserCourseId`),
    UNIQUE KEY `UX_UserCourses_UserId_CourseId` (`UserId`, `CourseId`),
    KEY `IX_UserCourses_CourseId` (`CourseId`),
    CONSTRAINT `FK_UserCourses_Users_UserId`
        FOREIGN KEY (`UserId`) REFERENCES `Users` (`UserId`) ON DELETE CASCADE,
    CONSTRAINT `FK_UserCourses_Courses_CourseId`
        FOREIGN KEY (`CourseId`) REFERENCES `Courses` (`CourseId`) ON DELETE CASCADE
) ENGINE=InnoDB CHARACTER SET utf8mb4;");
    }

    private async Task<bool> TableExistsAsync(string tableName)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        try
        {
            if (shouldClose) await connection.OpenAsync();

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
            if (shouldClose && connection.State == ConnectionState.Open)
                await connection.CloseAsync();
        }
    }
}
