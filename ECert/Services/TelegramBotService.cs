using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ECert.Services;

/// <summary>
/// Thin client over the Telegram Bot API. The bot token is supplied by the caller (it is read from
/// the security settings), so the client holds no configuration of its own and every method is a
/// no-op returning false when no token is configured — the security features degrade quietly on an
/// installation that has not set Telegram up.
/// </summary>
public class TelegramBotService
{
    private readonly HttpClient _http;

    public TelegramBotService(HttpClient http) => _http = http;

    public bool IsConfigured(string? botToken) => !string.IsNullOrWhiteSpace(botToken);

    /// <summary>Sends a plain-text message to a chat. Returns false on any failure.</summary>
    public async Task<bool> SendMessageAsync(string? botToken, string? chatId, string text, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured(botToken) || string.IsNullOrWhiteSpace(chatId))
            return false;

        var payload = JsonSerializer.Serialize(new
        {
            chat_id = chatId,
            text,
            parse_mode = "HTML",
            disable_web_page_preview = true
        });

        return await PostAsync(botToken!, "sendMessage", payload, cancellationToken);
    }

    /// <summary>Uploads a document (used for the encrypted backup archive). Returns false on failure.</summary>
    public async Task<bool> SendDocumentAsync(string? botToken, string? chatId, string fileName, byte[] content, string? caption = null, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured(botToken) || string.IsNullOrWhiteSpace(chatId))
            return false;

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId!), "chat_id");
            if (!string.IsNullOrWhiteSpace(caption))
                form.Add(new StringContent(caption!), "caption");

            var fileContent = new ByteArrayContent(content);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "document", fileName);

            var response = await _http.PostAsync(Endpoint(botToken!, "sendDocument"), form, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads pending updates. Used by the account-linking poller to find a message whose text is the
    /// employee's link code. Returns an empty list when Telegram is not configured.
    /// </summary>
    public async Task<IReadOnlyList<TelegramUpdate>> GetUpdatesAsync(string? botToken, long offset, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured(botToken))
            return Array.Empty<TelegramUpdate>();

        try
        {
            var url = $"{Endpoint(botToken!, "getUpdates")}?offset={offset}&timeout=0&allowed_updates=%5B%22message%22%5D";
            var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode) return Array.Empty<TelegramUpdate>();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return Array.Empty<TelegramUpdate>();

            var updates = new List<TelegramUpdate>();
            foreach (var item in result.EnumerateArray())
            {
                var updateId = item.TryGetProperty("update_id", out var uid) ? uid.GetInt64() : 0;
                if (!item.TryGetProperty("message", out var message)) continue;
                var text = message.TryGetProperty("text", out var t) ? t.GetString() : null;
                var chatId = message.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var cid)
                    ? cid.GetInt64().ToString()
                    : null;
                if (chatId != null)
                    updates.Add(new TelegramUpdate(updateId, chatId, text ?? string.Empty));
            }

            return updates;
        }
        catch
        {
            return Array.Empty<TelegramUpdate>();
        }
    }

    private async Task<bool> PostAsync(string botToken, string method, string json, CancellationToken cancellationToken)
    {
        try
        {
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(Endpoint(botToken, method), content, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string Endpoint(string botToken, string method) =>
        $"https://api.telegram.org/bot{botToken}/{method}";
}

public sealed record TelegramUpdate(long UpdateId, string ChatId, string Text);
