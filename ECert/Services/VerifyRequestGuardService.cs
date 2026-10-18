using Microsoft.Extensions.Caching.Memory;

namespace ECert.Services;

public class VerifyRequestGuardService
{
    private const int PermitLimit = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(15);
    private readonly IMemoryCache _cache;

    public VerifyRequestGuardService(IMemoryCache cache)
    {
        _cache = cache;
    }

    public bool IsAllowed(HttpContext httpContext)
    {
        var clientIp = GetClientIp(httpContext);
        var cacheKey = $"verify-guard:{clientIp}";
        var state = _cache.GetOrCreate(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = BlockDuration + Window;
            return new VerifyRequestState
            {
                WindowStartedAtUtc = DateTime.UtcNow,
                Attempts = 0
            };
        })!;

        var now = DateTime.UtcNow;
        if (state.BlockedUntilUtc.HasValue && state.BlockedUntilUtc > now)
        {
            _cache.Set(cacheKey, state, state.BlockedUntilUtc.Value - now + Window);
            return false;
        }

        if (now - state.WindowStartedAtUtc >= Window)
        {
            state.WindowStartedAtUtc = now;
            state.Attempts = 0;
            state.BlockedUntilUtc = null;
        }

        state.Attempts++;

        if (state.Attempts > PermitLimit)
        {
            state.BlockedUntilUtc = now.Add(BlockDuration);
            _cache.Set(cacheKey, state, BlockDuration + Window);
            return false;
        }

        _cache.Set(cacheKey, state, Window + BlockDuration);
        return true;
    }

    private static string GetClientIp(HttpContext httpContext) => ClientAddressResolver.Resolve(httpContext);

    private sealed class VerifyRequestState
    {
        public DateTime WindowStartedAtUtc { get; set; }
        public int Attempts { get; set; }
        public DateTime? BlockedUntilUtc { get; set; }
    }
}
