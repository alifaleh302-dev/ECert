using System.Net;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Covers the security hardening package: the SuperAdmin-only settings dashboard, the encrypted
/// backup round-trip, the tamper-evident audit hash chain and the database guard that makes the
/// audit tables append-only.
/// </summary>
[Collection(AppCollection.Name)]
public class SecurityHardeningTests
{
    private readonly AppFixture _fixture;

    public SecurityHardeningTests(AppFixture fixture) => _fixture = fixture;

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    [Fact]
    public async Task Security_settings_page_loads_for_super_admin()
    {
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/SecuritySettings");

        // Arabic renders as HTML numeric entities, so assert on the stable ASCII markers.
        Assert.Contains("name=\"TelegramBotToken\"", html);
        Assert.Contains("name=\"BackupChannelId\"", html);
        Assert.Contains("/SecuritySettings/Save", html);
    }

    [Fact]
    public async Task Saving_security_settings_persists_them()
    {
        var admin = await SignInAdminAsync();
        var response = await admin.PostFormAsync("/SecuritySettings", "/SecuritySettings/Save",
            new Dictionary<string, string>
            {
                ["MaxFailedLoginAttempts"] = "7",
                ["LockoutMinutes"] = "20",
                ["IdleTimeoutMinutes"] = "45",
                ["BackupHourUtc"] = "3",
                ["SecurityAlertChannelId"] = "-1005550001",
                ["TwoFactorEnabled"] = "false",
                ["BackupEnabled"] = "true"
            });
        response.EnsureSuccessStatusCode();

        var settings = await _fixture.Factory.WithDbAsync(db =>
            db.SecuritySettings.AsNoTracking().FirstAsync(s => s.SecuritySettingsId == 1));
        Assert.Equal(7, settings.MaxFailedLoginAttempts);
        Assert.Equal(20, settings.LockoutMinutes);
        Assert.Equal(45, settings.IdleTimeoutMinutes);
        Assert.Equal("-1005550001", settings.SecurityAlertChannelId);
    }

    [Fact]
    public void Backup_crypto_round_trips_and_rejects_a_wrong_key()
    {
        var crypto = new BackupCryptoService();
        var payload = System.Text.Encoding.UTF8.GetBytes("CREATE TABLE t (id INT); -- سرّي");
        const string key = "correct horse battery staple";

        var archive = crypto.Encrypt(payload, key);
        Assert.NotEqual(payload, archive);

        var restored = crypto.Decrypt(archive, key);
        Assert.Equal(payload, restored);

        Assert.ThrowsAny<Exception>(() => crypto.Decrypt(archive, "wrong key"));
    }

    [Fact]
    public async Task Audit_log_hash_chain_verifies_after_activity()
    {
        var admin = await SignInAdminAsync();
        // Logging in writes an audit row; make sure there is at least one chained entry.
        await admin.GetStringAsync("/Dashboard");

        var result = await _fixture.Factory.WithDbAsync(async db =>
        {
            var service = new AuditIntegrityService(db);
            return await service.VerifyOperationLogAsync();
        });

        Assert.True(result.Intact, result.Detail);
        Assert.True(result.Checked > 0);
    }

    [Fact]
    public async Task Audit_tables_reject_update_and_delete()
    {
        var admin = await SignInAdminAsync();
        await admin.GetStringAsync("/Dashboard"); // guarantee a row exists

        var update = await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.Factory.ExecuteRawAsync("UPDATE AuditLogs SET Action = 'Tampered' WHERE AuditLogId = 1;"));
        Assert.Equal(1644, update.Number); // SIGNAL SQLSTATE '45000'

        var delete = await Assert.ThrowsAsync<MySqlException>(() =>
            _fixture.Factory.ExecuteRawAsync("DELETE FROM AuditLogs WHERE AuditLogId = 1;"));
        Assert.Equal(1644, delete.Number);
    }

    [Fact]
    public async Task Security_settings_page_is_denied_without_the_permission()
    {
        var response = await AppClient.Create(_fixture.Factory).GetAsync("/SecuritySettings");
        Assert.True(AppClient.IsDenied(response));
    }

    [Fact]
    public void Login_throttle_blocks_after_the_configured_failures()
    {
        var throttle = new LoginThrottleService();
        const string user = "throttle-probe";
        const string address = "10.99.99.99";

        for (var i = 0; i < SecurityPolicySnapshot.MaxFailedLoginAttempts; i++)
            throttle.RegisterFailure(user, address);

        Assert.True(throttle.IsBlocked(user, address, out var remaining));
        Assert.True(remaining > TimeSpan.Zero);
    }

    [Fact]
    public async Task Backup_dumps_and_encrypts_the_database()
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<SecuritySettingsService>();
        var settings = await settingsService.GetAsync();
        settings.BackupEncryptionKey = "integration-test-key";
        await settingsService.SaveAsync(settings, "test");

        var backup = scope.ServiceProvider.GetRequiredService<DatabaseBackupService>();
        var result = await backup.RunAsync();

        // No Telegram channel is configured for the tests, so the archive cannot be delivered —
        // but the dump and encryption must have succeeded for the message to reach that point.
        Assert.DoesNotContain("mysqldump", result);
        Assert.Contains("قناة الأرشيف", result);
    }
}
