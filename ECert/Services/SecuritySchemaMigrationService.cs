using System.Data;
using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Creates the <c>SecuritySettings</c> table, adds the Telegram columns to <c>Users</c> and the
/// hash-chain columns to both audit tables, and installs database triggers that reject any
/// <c>UPDATE</c> or <c>DELETE</c> on those tables. Because the guard lives in the database, a bug —
/// or an operator with a SQL client — cannot silently rewrite the audit trail; the tamper attempt
/// is turned into a hard error.
/// </summary>
public class SecuritySchemaMigrationService
{
    private readonly ECertDbContext _db;
    public SecuritySchemaMigrationService(ECertDbContext db) => _db = db;

    public async Task EnsureAsync()
    {
        await _db.Database.ExecuteSqlRawAsync(@"
            CREATE TABLE IF NOT EXISTS `SecuritySettings` (
                `SecuritySettingsId` INT NOT NULL,
                `TelegramBotToken` LONGTEXT NULL,
                `SecurityAlertChannelId` VARCHAR(64) NULL,
                `BackupChannelId` VARCHAR(64) NULL,
                `BackupEncryptionKey` LONGTEXT NULL,
                `TwoFactorEnabled` TINYINT(1) NOT NULL DEFAULT 0,
                `MaxFailedLoginAttempts` INT NOT NULL DEFAULT 5,
                `LockoutMinutes` INT NOT NULL DEFAULT 15,
                `IdleTimeoutMinutes` INT NOT NULL DEFAULT 30,
                `BackupEnabled` TINYINT(1) NOT NULL DEFAULT 1,
                `BackupHourUtc` INT NOT NULL DEFAULT 2,
                `UpdatedAt` DATETIME(6) NOT NULL,
                `UpdatedBy` VARCHAR(150) NULL,
                PRIMARY KEY (`SecuritySettingsId`)
            ) CHARACTER SET utf8mb4;");

        await AddColumnIfMissingAsync("Users", "TelegramChatId", "VARCHAR(40) NULL");
        await AddColumnIfMissingAsync("Users", "TelegramLinkCode", "VARCHAR(20) NULL");
        await AddColumnIfMissingAsync("Users", "TelegramLinkedAt", "DATETIME(6) NULL");

        await AddColumnIfMissingAsync("AuditLogs", "PreviousHash", "VARCHAR(64) NULL");
        await AddColumnIfMissingAsync("AuditLogs", "RowHash", "VARCHAR(64) NULL");
        await AddColumnIfMissingAsync("FinancialAuditEntries", "PreviousHash", "VARCHAR(64) NULL");
        await AddColumnIfMissingAsync("FinancialAuditEntries", "RowHash", "VARCHAR(64) NULL");

        // The backfill writes UPDATEs, so it must run while the immutability triggers are absent.
        // Dropping and recreating them on every boot also keeps this idempotent.
        await DropImmutableTriggersAsync("AuditLogs");
        await DropImmutableTriggersAsync("FinancialAuditEntries");

        await BackfillAuditLogsAsync();
        await BackfillFinancialAuditsAsync();

        await EnsureImmutableTriggersAsync("AuditLogs");
        await EnsureImmutableTriggersAsync("FinancialAuditEntries");
    }

    /// <summary>
    /// Gives every pre-existing audit row a hash so the chain is complete from the first row on.
    /// It uses the same <see cref="AuditHashChain"/> serialisation as the runtime writer and the
    /// verifier, so the hashes agree. Already-hashed rows are skipped and the chain continues from
    /// the last stored hash.
    /// </summary>
    private async Task BackfillAuditLogsAsync()
    {
        var rows = await _db.AuditLogs.OrderBy(l => l.AuditLogId).ToListAsync();
        var previous = string.Empty;
        var changed = false;
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.RowHash))
            {
                previous = row.RowHash;
                continue;
            }

            row.Timestamp = AuditHashChain.Normalize(row.Timestamp);
            row.PreviousHash = previous;
            row.RowHash = AuditHashChain.Compute(row, previous);
            previous = row.RowHash;
            changed = true;
        }

        if (changed) await _db.SaveChangesAsync();
    }

    private async Task BackfillFinancialAuditsAsync()
    {
        var rows = await _db.FinancialAuditEntries.OrderBy(e => e.FinancialAuditEntryId).ToListAsync();
        var previous = string.Empty;
        var changed = false;
        foreach (var row in rows)
        {
            if (!string.IsNullOrEmpty(row.RowHash))
            {
                previous = row.RowHash;
                continue;
            }

            row.Timestamp = AuditHashChain.Normalize(row.Timestamp);
            row.PreviousHash = previous;
            row.RowHash = AuditHashChain.Compute(row, previous);
            previous = row.RowHash;
            changed = true;
        }

        if (changed) await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Installs <c>BEFORE UPDATE</c> and <c>BEFORE DELETE</c> triggers that raise an error. MySQL
    /// has no <c>CREATE TRIGGER IF NOT EXISTS</c>, so each is dropped before being recreated;
    /// re-running is therefore safe and always converges on the current definition.
    /// </summary>
    private async Task EnsureImmutableTriggersAsync(string table)
    {
        foreach (var (trigger, eventName) in Triggers(table))
        {
            await _db.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS `{trigger}`;");
            await _db.Database.ExecuteSqlRawAsync($@"
                CREATE TRIGGER `{trigger}` BEFORE {eventName} ON `{table}`
                FOR EACH ROW
                BEGIN
                    SIGNAL SQLSTATE '45000'
                        SET MESSAGE_TEXT = 'سجلات التدقيق محصّنة: لا يُسمح بالتعديل أو الحذف';
                END;");
        }
    }

    private async Task DropImmutableTriggersAsync(string table)
    {
        foreach (var (trigger, _) in Triggers(table))
            await _db.Database.ExecuteSqlRawAsync($"DROP TRIGGER IF EXISTS `{trigger}`;");
    }

    private static (string Trigger, string Event)[] Triggers(string table) => new[]
    {
        ($"trg_{table}_no_update", "UPDATE"),
        ($"trg_{table}_no_delete", "DELETE")
    };

    private async Task AddColumnIfMissingAsync(string table, string column, string definition)
    {
        if (await ColumnExistsAsync(table, column))
            return;
        await _db.Database.ExecuteSqlRawAsync($"ALTER TABLE `{table}` ADD COLUMN `{column}` {definition};");
    }

    private async Task<bool> ColumnExistsAsync(string table, string column)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose) await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM information_schema.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @column";
            AddParameter(command, "@table", table);
            AddParameter(command, "@column", column);
            return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
        }
        finally
        {
            if (shouldClose) await connection.CloseAsync();
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
