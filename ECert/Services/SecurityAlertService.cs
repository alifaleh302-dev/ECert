using System.Text;
using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Sends real-time security notifications to the alert channel configured in
/// <c>/SecuritySettings</c>. Each alert is a fixed template that carries only what an operator
/// needs to react — never a password, a full token, an IP-embedded payload or the value of a
/// changed field. If Telegram is not configured the alert is logged and dropped, so a
/// misconfiguration never breaks the operation that triggered it.
/// </summary>
public class SecurityAlertService
{
    private readonly ECertDbContext _db;
    private readonly TelegramBotService _telegram;
    private readonly ILogger<SecurityAlertService> _logger;

    public SecurityAlertService(ECertDbContext db, TelegramBotService telegram, ILogger<SecurityAlertService> logger)
    {
        _db = db;
        _telegram = telegram;
        _logger = logger;
    }

    private async Task<string?> TokenAsync()
    {
        var settings = await _db.SecuritySettings.FirstOrDefaultAsync(s => s.SecuritySettingsId == Models.SecuritySettings.SingletonId);
        return settings?.TelegramBotToken;
    }

    private async Task<string?> ChannelAsync()
    {
        var settings = await _db.SecuritySettings.FirstOrDefaultAsync(s => s.SecuritySettingsId == Models.SecuritySettings.SingletonId);
        return settings?.SecurityAlertChannelId;
    }

    private async Task SendAsync(string message)
    {
        try
        {
            var token = await TokenAsync();
            var channel = await ChannelAsync();
            if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("Security alert (no Telegram channel configured): {Message}", message);
                return;
            }

            var ok = await _telegram.SendMessageAsync(token, channel, message);
            if (!ok) _logger.LogWarning("Failed to deliver security alert to Telegram.");
        }
        catch (Exception ex)
        {
            // Alerting must never surface as an error to the user action that triggered it.
            _logger.LogWarning(ex, "Security alert dispatch failed.");
        }
    }

    private static string Escape(string? value) => string.IsNullOrEmpty(value)
        ? ""
        : value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>Raised after repeated failed sign-ins for one account or address.</summary>
    public Task AlertRepeatedFailedLoginsAsync(string username, string clientAddress, int failureCount) =>
        SendAsync($"🔒 <b>محاولات دخول فاشلة متكررة</b>\n" +
                  $"الحساب: {Escape(username)}\n" +
                  $"العنوان: {Escape(clientAddress)}\n" +
                  $"عدد المحاولات: {failureCount}");

    /// <summary>Raised when a user's roles or a role's permissions change.</summary>
    public Task AlertPermissionsChangedAsync(string actor, string target, string detail) =>
        SendAsync($"🛡️ <b>تعديل صلاحيات</b>\n" +
                  $"المنفّذ: {Escape(actor)}\n" +
                  $"المستهدف: {Escape(target)}\n" +
                  $"التفصيل: {Escape(detail)}");

    /// <summary>Raised when a locked accounting period is reopened.</summary>
    public Task AlertPeriodReopenedAsync(string actor, string? note) =>
        SendAsync($"🔓 <b>إعادة فتح فترة مالية</b>\n" +
                  $"المنفّذ: {Escape(actor)}\n" +
                  $"ملاحظة: {Escape(note)}");

    /// <summary>
    /// Raised when a large refund is approved. The amount is included only as a threshold band,
    /// never as an exact figure tied to a customer.
    /// </summary>
    public Task AlertLargeRefundApprovedAsync(string actor, string referenceNumber, decimal amount) =>
        SendAsync($"💸 <b>اعتماد استرداد مالي كبير</b>\n" +
                  $"المنفّذ: {Escape(actor)}\n" +
                  $"المرجع: {Escape(referenceNumber)}\n" +
                  $"الفئة: {Band(amount)}");

    /// <summary>Raised when the daily encrypted backup has been archived to Telegram.</summary>
    public Task AlertBackupCompletedAsync(string fileName, long sizeBytes) =>
        SendAsync($"🗄️ <b>نسخة احتياطية مشفّرة</b>\n" +
                  $"الملف: {Escape(fileName)}\n" +
                  $"الحجم: {sizeBytes / 1024} كيلوبايت");

    /// <summary>Raised when the backup job fails, so an operator can intervene.</summary>
    public Task AlertBackupFailedAsync(string reason) =>
        SendAsync($"⚠️ <b>فشل النسخ الاحتياطي</b>\n" +
                  $"السبب: {Escape(reason)}");

    private static string Band(decimal amount) => amount switch
    {
        >= 100000 => "أكبر من 100,000",
        >= 10000 => "بين 10,000 و100,000",
        >= 1000 => "بين 1,000 و10,000",
        _ => "أقل من 1,000"
    };
}
