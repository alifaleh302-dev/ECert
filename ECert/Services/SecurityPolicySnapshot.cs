namespace ECert.Services;

/// <summary>
/// A process-wide snapshot of the values that the request pipeline needs on the hot path — the
/// login lockout policy, the idle timeout and whether 2FA is on. The rate limiter and the login
/// guard read these without resolving a scoped service, and <see cref="SecuritySettingsService"/>
/// refreshes the snapshot whenever the settings row is loaded or saved, so a change made in
/// <c>/SecuritySettings</c> takes effect within one cache interval.
/// </summary>
public static class SecurityPolicySnapshot
{
    private static int _maxFailedLoginAttempts = 5;
    private static int _lockoutMinutes = 15;
    private static int _idleTimeoutMinutes = 30;
    private static int _twoFactorEnabled;

    public static int MaxFailedLoginAttempts => Volatile.Read(ref _maxFailedLoginAttempts);
    public static int LockoutMinutes => Volatile.Read(ref _lockoutMinutes);
    public static int IdleTimeoutMinutes => Volatile.Read(ref _idleTimeoutMinutes);
    public static bool TwoFactorEnabled => Volatile.Read(ref _twoFactorEnabled) == 1;

    public static void Update(int maxFailedLoginAttempts, int lockoutMinutes, int idleTimeoutMinutes, bool twoFactorEnabled)
    {
        Volatile.Write(ref _maxFailedLoginAttempts, Math.Clamp(maxFailedLoginAttempts, 1, 100));
        Volatile.Write(ref _lockoutMinutes, Math.Clamp(lockoutMinutes, 1, 1440));
        Volatile.Write(ref _idleTimeoutMinutes, Math.Clamp(idleTimeoutMinutes, 1, 1440));
        Volatile.Write(ref _twoFactorEnabled, twoFactorEnabled ? 1 : 0);
    }
}
