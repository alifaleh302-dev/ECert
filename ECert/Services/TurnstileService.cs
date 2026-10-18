namespace ECert.Services;

/// <summary>
/// Verifies Cloudflare Turnstile challenges for the anonymous public endpoints
/// (course registration and certificate verification).
/// </summary>
public sealed class TurnstileService
{
    private const string VerifyEndpoint = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    private const string ResponseField = "cf-turnstile-response";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TurnstileService> _logger;
    private readonly string? _siteKey;
    private readonly string? _secretKey;

    public TurnstileService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<TurnstileService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _siteKey = configuration["TURNSTILE_SITE_KEY"] ?? configuration["CertificateSecurity:TurnstileSiteKey"];
        _secretKey = configuration["TURNSTILE_SECRET_KEY"] ?? configuration["CertificateSecurity:TurnstileSecretKey"];
    }

    /// <summary>True when both keys are configured and the widget should be rendered.</summary>
    public bool IsEnabled =>
        !string.IsNullOrWhiteSpace(_siteKey) && !string.IsNullOrWhiteSpace(_secretKey);

    public string? SiteKey => IsEnabled ? _siteKey : null;

    /// <summary>
    /// Reads the challenge token from the posted form and verifies it.
    /// </summary>
    public Task<bool> VerifyRequestAsync(HttpContext context) =>
        VerifyAsync(context.Request.Form[ResponseField].FirstOrDefault(), context);

    /// <summary>
    /// Validates a challenge token. When Turnstile is not configured the endpoint stays
    /// usable and the request is allowed through, so a missing key cannot silently disable
    /// a core flow such as registration. Callers must still apply their own abuse controls
    /// (rate limiting and the honeypot field), which remain active either way.
    /// </summary>
    public async Task<bool> VerifyAsync(string? token, HttpContext context)
    {
        if (!IsEnabled)
            return true;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            using var client = _httpClientFactory.CreateClient();
            using var response = await client.PostAsync(VerifyEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["secret"] = _secretKey!,
                ["response"] = token,
                ["remoteip"] = ClientAddressResolver.Resolve(context)
            }));

            response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<TurnstileVerifyResponse>();
            return payload?.Success == true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Turnstile verification request failed; rejecting the submission.");
            return false;
        }
    }

    private sealed record TurnstileVerifyResponse(bool Success);
}
