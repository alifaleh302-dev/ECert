using System.Net;

namespace ECert.Services;

/// <summary>
/// Resolves the real client address behind the hosting platform's reverse proxy.
/// </summary>
public static class ClientAddressResolver
{
    /// <summary>
    /// Returns the client address for rate limiting and audit records.
    /// The forwarding middleware runs first and rewrites <c>RemoteIpAddress</c> from the
    /// trusted proxy chain, so that value is authoritative. The raw header is only a
    /// fallback, and the <em>rightmost</em> entry is used: each proxy appends the address
    /// of the peer that connected to it, so the leftmost entries are caller-supplied and
    /// can be forged to evade per-address throttling.
    /// </summary>
    public static string Resolve(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null)
            return Normalize(remote);

        var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(forwarded))
        {
            var entries = forwarded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (entries.Length > 0 && IPAddress.TryParse(entries[^1], out var parsed))
                return Normalize(parsed);
        }

        return "unknown";
    }

    private static string Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString() : address.ToString();
}
