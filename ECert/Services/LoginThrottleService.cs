using System.Collections.Concurrent;

namespace ECert.Services;

/// <summary>
/// Enforces the login lockout policy configured in <c>/SecuritySettings</c>. A failed attempt
/// counts against both the account and the client address; once either reaches
/// <see cref="SecurityPolicySnapshot.MaxFailedLoginAttempts"/> the identity is blocked for
/// <see cref="SecurityPolicySnapshot.LockoutMinutes"/>. State is kept in memory (the process is
/// the unit of protection here), with an escalating penalty for repeated lockouts so a sustained
/// guessing campaign gets progressively more expensive.
/// </summary>
public sealed class LoginThrottleService
{
    private sealed record AttemptState(int Failures, DateTimeOffset WindowStarted, DateTimeOffset? BlockedUntil, int Lockouts);

    private readonly ConcurrentDictionary<string, AttemptState> _states = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int MaxTrackedKeys = 20_000;

    public bool IsBlocked(string username, string clientAddress, out TimeSpan remaining) =>
        IsKeyBlocked(AccountKey(username), out remaining) || IsKeyBlocked(AddressKey(clientAddress), out remaining);

    public void RegisterFailure(string username, string clientAddress)
    {
        RegisterFailureByKey(AccountKey(username));
        RegisterFailureByKey(AddressKey(clientAddress));
    }

    /// <summary>Clears the counters for a successful sign-in.</summary>
    public void RegisterSuccess(string username, string clientAddress)
    {
        _states.TryRemove(AccountKey(username), out _);
        _states.TryRemove(AddressKey(clientAddress), out _);
    }

    private void RegisterFailureByKey(string key)
    {
        var now = DateTimeOffset.UtcNow;
        _states.AddOrUpdate(
            key,
            _ => new AttemptState(1, now, null, 0),
            (_, current) =>
            {
                if (current.BlockedUntil is { } until && until > now)
                    return current; // already blocked; do not extend on every probe

                var expired = now - current.WindowStarted >= Window;
                var failures = (expired ? 0 : current.Failures) + 1;
                var windowStart = expired ? now : current.WindowStarted;

                if (failures >= SecurityPolicySnapshot.MaxFailedLoginAttempts)
                {
                    // Exponential back-off: each repeat lockout lasts longer, capped at a day.
                    var lockouts = current.Lockouts + 1;
                    var minutes = SecurityPolicySnapshot.LockoutMinutes * Math.Pow(2, Math.Min(lockouts - 1, 5));
                    minutes = Math.Min(minutes, 1440);
                    return new AttemptState(failures, windowStart, now.AddMinutes(minutes), lockouts);
                }

                return current with { Failures = failures, WindowStarted = windowStart };
            });

        TrimExpiredEntries();
    }

    private bool IsKeyBlocked(string key, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_states.TryGetValue(key, out var state))
            return false;

        if (state.BlockedUntil is { } until && until > DateTimeOffset.UtcNow)
        {
            remaining = until - DateTimeOffset.UtcNow;
            return true;
        }

        if (state.BlockedUntil != null || (DateTimeOffset.UtcNow - state.WindowStarted) >= Window)
            _states.TryRemove(key, out _);

        return false;
    }

    private void TrimExpiredEntries()
    {
        if (_states.Count <= MaxTrackedKeys)
            return;

        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _states)
        {
            var state = pair.Value;
            var blocked = state.BlockedUntil is { } until && until > now;
            if (!blocked && now - state.WindowStarted >= Window)
                _states.TryRemove(pair.Key, out _);
        }
    }

    private static string AccountKey(string username) => $"acct:{username.Trim().ToUpperInvariant()}";
    private static string AddressKey(string address) => $"addr:{address}";
}
