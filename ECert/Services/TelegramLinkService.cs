using System.Security.Cryptography;
using ECert.Data;
using Microsoft.EntityFrameworkCore;

namespace ECert.Services;

/// <summary>
/// Binds an employee's Telegram chat to their account. The administrator (or the employee) starts
/// the flow, which generates a short one-time code stored on the user row; the employee sends that
/// code to the bot and the poller matches it, recording the chat id. Only then can the account
/// receive 2FA codes or alerts.
/// </summary>
public class TelegramLinkService
{
    private readonly ECertDbContext _db;
    private readonly TelegramBotService _telegram;

    public TelegramLinkService(ECertDbContext db, TelegramBotService telegram)
    {
        _db = db;
        _telegram = telegram;
    }

    private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Generates and stores a fresh link code for a user, returning it.</summary>
    public async Task<string> GenerateCodeAsync(int userId)
    {
        var user = await _db.Users.FindAsync(userId) ?? throw new InvalidOperationException("المستخدم غير موجود.");
        var code = GenerateCode();
        user.TelegramLinkCode = code;
        user.TelegramChatId = null;
        user.TelegramLinkedAt = null;
        await _db.SaveChangesAsync();
        return code;
    }

    /// <summary>Clears a user's Telegram binding.</summary>
    public async Task UnlinkAsync(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return;
        user.TelegramChatId = null;
        user.TelegramLinkCode = null;
        user.TelegramLinkedAt = null;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Reads pending bot updates once and links any user whose stored code matches an incoming
    /// message. Returns the number of accounts linked.
    /// </summary>
    public async Task<int> PollOnceAsync(long offset, CancellationToken cancellationToken = default)
    {
        var token = (await _db.SecuritySettings.FirstOrDefaultAsync(s => s.SecuritySettingsId == Models.SecuritySettings.SingletonId, cancellationToken))?.TelegramBotToken;
        if (string.IsNullOrWhiteSpace(token))
            return 0;

        var updates = await _telegram.GetUpdatesAsync(token, offset, cancellationToken);
        if (updates.Count == 0)
            return 0;

        var linked = 0;
        foreach (var update in updates)
        {
            var code = update.Text.Trim().ToUpperInvariant();
            if (code.Length is < 4 or > 20) continue;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.TelegramLinkCode == code, cancellationToken);
            if (user == null) continue;

            user.TelegramChatId = update.ChatId;
            user.TelegramLinkCode = null;
            user.TelegramLinkedAt = DateTime.Now;
            linked++;
            await _db.SaveChangesAsync();
            await _telegram.SendMessageAsync(token, update.ChatId, "✅ تم ربط حسابك بمركز التدريب بنجاح.", cancellationToken);
        }

        return linked;
    }

    private static string GenerateCode()
    {
        var bytes = RandomNumberGenerator.GetBytes(8);
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = CodeAlphabet[bytes[i] % CodeAlphabet.Length];
        return new string(chars);
    }
}
