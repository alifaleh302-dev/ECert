using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Adds indexes on the columns the application filters and sorts by most often, plus the
/// <c>Users.MustChangePassword</c> column. Every statement is idempotent, so it is safe to
/// run on each startup.
/// </summary>
public class PerformanceIndexSchemaMigrationService
{
    private readonly ECertDbContext _db;
    public PerformanceIndexSchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await AddColumnIfMissingAsync("Users", "MustChangePassword", "TINYINT(1) NOT NULL DEFAULT 0");

        // Status/type columns are mapped as unbounded strings, which MySQL stores as LONGTEXT.
        // A LONGTEXT column cannot be used in a key without a prefix length, so every index
        // below on such a column silently failed to create. Narrow them to VARCHAR(40), which
        // also matches the [StringLength(40)] annotation now declared on the model properties.
        foreach (var column in new[] { "Status", "PostType" })
            await NarrowTextColumnAsync("Posts", column, 40);

        foreach (var table in new[] { "Registrations", "Courses", "Invoices", "Notifications" })
            await NarrowTextColumnAsync(table, "Status", 40);

        // MySQL has no CREATE INDEX IF NOT EXISTS, so each index is guarded by a check
        // against information_schema before it is created.
        var indexes = new (string Table, string Name, string Columns)[]
        {
            ("Registrations", "IX_Registrations_Status_RegistrationDate", "`Status`, `RegistrationDate`"),
            ("Registrations", "IX_Registrations_CourseId_Status", "`CourseId`, `Status`"),
            ("Courses", "IX_Courses_Status_CreatedAt", "`Status`, `CreatedAt`"),
            ("Courses", "IX_Courses_CategoryId", "`CategoryId`"),
            ("Invoices", "IX_Invoices_Status_CreatedAt", "`Status`, `CreatedAt`"),
            ("Invoices", "IX_Invoices_RegistrationId", "`RegistrationId`"),
            ("Payments", "IX_Payments_InvoiceId", "`InvoiceId`"),
            ("AuditLogs", "IX_AuditLogs_Timestamp", "`Timestamp`"),
            ("Notifications", "IX_Notifications_Status_CreatedAt", "`Status`, `CreatedAt`"),
            ("Certificates", "IX_Certificates_RegistrationId", "`RegistrationId`"),
            // Posts are filtered by PostType (news vs posts) and ordered by Status/date; the
            // table has no IsPublished column.
            ("Posts", "IX_Posts_PostType_Status", "`PostType`, `Status`")
        };

        foreach (var (table, name, columns) in indexes)
            await CreateIndexIfMissingAsync(table, name, columns);
    }

    /// <summary>
    /// Converts a text/longtext column to <c>VARCHAR(length)</c> so it can participate in an
    /// index. No-op once the column is already a bounded varchar.
    /// </summary>
    private async Task NarrowTextColumnAsync(string table, string column, int length)
    {
        if (!await ColumnExistsAsync(table, column))
            return;

        var currentType = await GetColumnTypeAsync(table, column);
        if (currentType is null || currentType.StartsWith("varchar", StringComparison.OrdinalIgnoreCase))
            return;

        if (!currentType.Contains("text", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await _db.Database.ExecuteSqlRawAsync(
                $"ALTER TABLE `{table}` MODIFY COLUMN `{column}` VARCHAR({length}) NOT NULL;");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Skipped narrowing {table}.{column}: {ex.Message}");
        }
    }

    private async Task<string?> GetColumnTypeAsync(string table, string column)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT DATA_TYPE FROM information_schema.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column";
            AddParameter(command, "@table", table);
            AddParameter(command, "@column", column);
            var result = await command.ExecuteScalarAsync();
            return result as string;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private async Task AddColumnIfMissingAsync(string table, string column, string definition)
    {
        if (await ColumnExistsAsync(table, column))
            return;

        await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition};");
    }

    private async Task CreateIndexIfMissingAsync(string table, string indexName, string columns)
    {
        if (await IndexExistsAsync(table, indexName))
            return;

        // A missing table or column (an older deployment) must not abort startup.
        try
        {
            await _db.Database.ExecuteSqlRawAsync($"CREATE INDEX `{indexName}` ON `{table}` ({columns});");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Skipped index {indexName} on {table}: {ex.Message}");
        }
    }

    private async Task<bool> IndexExistsAsync(string table, string indexName)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM information_schema.STATISTICS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND INDEX_NAME = @index";
            AddParameter(command, "@table", table);
            AddParameter(command, "@index", indexName);
            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt64(result) > 0;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private async Task<bool> ColumnExistsAsync(string table, string column)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM information_schema.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column";
            AddParameter(command, "@table", table);
            AddParameter(command, "@column", column);
            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt64(result) > 0;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
