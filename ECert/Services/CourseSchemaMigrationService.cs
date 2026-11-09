using System.Data;
using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Reconciles the Courses table with the current EF model. Databases created by an older schema
/// still carry TotalSeats/BookedSeats as INT NOT NULL columns without defaults, so any EF INSERT
/// that does not list them (e.g. bulk course import) fails with "Field ... doesn't have a default
/// value". This migration adds the columns when missing and back-fills a default when present.
/// </summary>
public sealed class CourseSchemaMigrationService
{
    private readonly ECertDbContext _db;

    public CourseSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await EnsureColumnAsync("TotalSeats", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync("BookedSeats", "INT NOT NULL DEFAULT 0");
        await EnsureColumnAsync("PartnerName", "VARCHAR(150) NULL");
        await EnsureColumnAsync("PartnerLogoUrl", "VARCHAR(400) NULL");
    }

    private async Task EnsureColumnAsync(string columnName, string definitionSql)
    {
        if (!await TableExistsAsync("Courses"))
            return;

        if (await ColumnExistsAsync("Courses", columnName))
        {
            // Column exists (possibly NOT NULL without a default). Re-define it with an explicit
            // default so inserts that omit it never fail; existing values are preserved.
            await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE `Courses` MODIFY COLUMN `{columnName}` {definitionSql};");
            return;
        }

        await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE `Courses` ADD COLUMN `{columnName}` {definitionSql};");
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
