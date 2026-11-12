using System.Diagnostics;
using ECert.Data;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;

namespace ECert.Services;

/// <summary>
/// Produces an encrypted, compressed snapshot of the whole MySQL database and hands it to the
/// archive channel. The dump is streamed through memory and never written to the container's disk,
/// so there is no local copy of the archive for an intruder to find; the only persistent copy is
/// the encrypted document in Telegram.
/// </summary>
public class DatabaseBackupService
{
    private readonly ECertDbContext _db;
    private readonly BackupCryptoService _crypto;
    private readonly TelegramBotService _telegram;
    private readonly SecurityAlertService _alerts;
    private readonly ILogger<DatabaseBackupService> _logger;

    public DatabaseBackupService(
        ECertDbContext db,
        BackupCryptoService crypto,
        TelegramBotService telegram,
        SecurityAlertService alerts,
        ILogger<DatabaseBackupService> logger)
    {
        _db = db;
        _crypto = crypto;
        _telegram = telegram;
        _alerts = alerts;
        _logger = logger;
    }

    /// <summary>
    /// Runs one backup cycle. Returns a short status string for the manual "run now" action.
    /// </summary>
    public async Task<string> RunAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _db.SecuritySettings.FirstOrDefaultAsync(s => s.SecuritySettingsId == Models.SecuritySettings.SingletonId, cancellationToken);
        if (settings == null)
            return "لا توجد إعدادات أمنية.";

        if (string.IsNullOrWhiteSpace(settings.BackupEncryptionKey))
            return "مفتاح التشفير غير مضبوط؛ لا يمكن إنشاء نسخة احتياطية.";

        byte[] dump;
        try
        {
            dump = await DumpDatabaseAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Database dump failed.");
            await _alerts.AlertBackupFailedAsync("تعذّر تنفيذ mysqldump");
            return "فشل تنفيذ النسخة الاحتياطية (mysqldump).";
        }

        var encrypted = _crypto.Encrypt(dump, settings.BackupEncryptionKey!);
        var fileName = $"ecert-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.enc";

        if (string.IsNullOrWhiteSpace(settings.BackupChannelId) || string.IsNullOrWhiteSpace(settings.TelegramBotToken))
        {
            _logger.LogWarning("Backup produced ({Bytes} bytes) but no Telegram archive channel is configured.", encrypted.Length);
            return "أُنشئت النسخة لكن قناة الأرشيف غير مضبوطة.";
        }

        var sent = await _telegram.SendDocumentAsync(settings.TelegramBotToken, settings.BackupChannelId, fileName, encrypted,
            caption: $"نسخة احتياطية مشفّرة AES-256 — {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC", cancellationToken: cancellationToken);

        if (!sent)
        {
            await _alerts.AlertBackupFailedAsync("تعذّر رفع النسخة إلى قناة الأرشيف");
            return "فشل رفع النسخة إلى قناة الأرشيف.";
        }

        await _alerts.AlertBackupCompletedAsync(fileName, encrypted.Length);
        return $"تم إنشاء نسخة احتياطية مشفّرة ({encrypted.Length / 1024} كيلوبايت) وإرسالها إلى قناة الأرشيف.";
    }

    /// <summary>
    /// Streams <c>mysqldump</c> output into memory. The password is passed via the <c>MYSQL_PWD</c>
    /// environment variable rather than the command line, so it is not exposed in the process list.
    /// </summary>
    private async Task<byte[]> DumpDatabaseAsync(CancellationToken cancellationToken)
    {
        var builder = new MySqlConnectionStringBuilder(_db.Database.GetConnectionString());
        var database = builder.Database;

        var startInfo = new ProcessStartInfo
        {
            FileName = "mysqldump",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add($"--host={builder.Server}");
        startInfo.ArgumentList.Add($"--port={builder.Port}");
        startInfo.ArgumentList.Add($"--user={builder.UserID}");
        startInfo.ArgumentList.Add("--single-transaction");
        startInfo.ArgumentList.Add("--routines");
        startInfo.ArgumentList.Add("--triggers");
        startInfo.ArgumentList.Add("--skip-lock-tables");
        startInfo.ArgumentList.Add("--default-character-set=utf8mb4");
        startInfo.ArgumentList.Add(database);
        startInfo.Environment["MYSQL_PWD"] = builder.Password;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("تعذّر تشغيل mysqldump.");

        using var buffer = new MemoryStream();
        var copyTask = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await Task.WhenAll(copyTask, errorTask);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"mysqldump exited with {process.ExitCode}: {errorTask.Result}");

        return buffer.ToArray();
    }
}
