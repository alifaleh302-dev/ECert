using System.Text.Json;

namespace ECert.Services.Excel;

/// <summary>
/// Holds a parsed import preview between the upload request and the confirm request.
/// Previews are written to a per-application temp directory under a GUID token and expire after
/// one hour; reading a preview consumes it so a token cannot be replayed.
/// </summary>
public static class ExcelPreviewStore
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    public static string Save<TPayload>(string category, TPayload payload)
    {
        var token = Guid.NewGuid().ToString("N");
        var path = PathFor(category, token);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return token;
    }

    /// <summary>Reads and deletes the stored preview. Returns null when the token is unknown.</summary>
    public static TPayload? Take<TPayload>(string category, string? token) where TPayload : class
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 32) return null;

        var path = PathFor(category, token);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<TPayload>(File.ReadAllText(path));
        }
        finally
        {
            TryDelete(path);
        }
    }

    public static bool IsExpired(DateTime createdAtUtc) => createdAtUtc < DateTime.UtcNow - Lifetime;

    private static string PathFor(string category, string token)
        => Path.Combine(Path.GetTempPath(), category, token + ".json");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* the temp file will be reclaimed by the OS */ }
    }
}
