using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ECert.Services;

/// <summary>
/// Holds the short-lived one-time passwords that confirm a sign-in when 2FA is enabled. Codes are
/// generated with a cryptographic RNG, valid for a few minutes and consumed on first use. State is
/// in memory: a pending challenge is inherently short-lived and does not need to survive a restart.
/// </summary>
public sealed class LoginOtpService
{
    private sealed record Challenge(string CodeHash, DateTimeOffset ExpiresAt, int Attempts);

    private readonly ConcurrentDictionary<int, Challenge> _challenges = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private const int MaxAttempts = 5;

    /// <summary>Issues a fresh code for a user id and returns the plaintext code.</summary>
    public string Issue(int userId)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        _challenges[userId] = new Challenge(Hash(code), DateTimeOffset.UtcNow.Add(Ttl), 0);
        return code;
    }

    /// <summary>True when a valid code was supplied; consumes the challenge on success.</summary>
    public bool Verify(int userId, string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || !_challenges.TryGetValue(userId, out var challenge))
            return false;

        if (DateTimeOffset.UtcNow > challenge.ExpiresAt)
        {
            _challenges.TryRemove(userId, out _);
            return false;
        }

        if (challenge.Attempts + 1 >= MaxAttempts)
            _challenges.TryRemove(userId, out _);

        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(Hash(code.Trim())),
                System.Text.Encoding.UTF8.GetBytes(challenge.CodeHash)))
        {
            _challenges[userId] = challenge with { Attempts = challenge.Attempts + 1 };
            return false;
        }

        _challenges.TryRemove(userId, out _);
        return true;
    }

    public void Clear(int userId) => _challenges.TryRemove(userId, out _);

    private static string Hash(string code) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(code)));
}
