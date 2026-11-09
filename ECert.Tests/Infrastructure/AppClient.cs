using System.Net;
using System.Text.RegularExpressions;

namespace ECert.Tests.Infrastructure;

/// <summary>
/// A thin, cookie-aware HTTP client that drives the real app the way a browser does: it reads the
/// antiforgery token from a page, posts forms, follows redirects and reports the sections a signed-in
/// user can actually open.
/// </summary>
public sealed class AppClient
{
    private static readonly Regex TokenPattern =
        new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    private readonly HttpClient _http;

    public AppClient(HttpClient http)
    {
        _http = http;
        // The login endpoint is rate limited per client address. Giving every client a distinct
        // address keeps the tests from tripping the limit while leaving the policy itself untouched.
        _http.DefaultRequestHeaders.TryAddWithoutValidation(
            "X-Forwarded-For", $"10.0.{Random.Shared.Next(0, 255)}.{Random.Shared.Next(1, 254)}");
    }

    /// <summary>Follows redirects so tests observe the page the user actually lands on.</summary>
    public static AppClient Create(TestAppFactory factory) =>
        new(factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = true,
            HandleCookies = true
        }));

    private async Task<string> GetTokenAsync(string path)
    {
        var html = await _http.GetStringAsync(path);
        var match = TokenPattern.Match(html);
        if (!match.Success)
            throw new InvalidOperationException($"No antiforgery token found on {path}.");
        return match.Groups[1].Value;
    }

    /// <summary>Signs in and returns the path the server redirected to after a successful login.</summary>
    public async Task<string> LoginAsync(string username, string password)
    {
        var token = await GetTokenAsync("/Auth/Login");
        var response = await _http.PostAsync("/Auth/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = username,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token
        }));
        response.EnsureSuccessStatusCode();
        return response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
    }

    public async Task<HttpResponseMessage> GetAsync(string path) => await _http.GetAsync(path);

    public async Task<string> GetStringAsync(string path) => await _http.GetStringAsync(path);

    /// <summary>Returns true when the path renders without an access-denied or login redirect.</summary>
    public async Task<bool> CanOpenAsync(string path)
    {
        var response = await _http.GetAsync(path);
        if (response.StatusCode is HttpStatusCode.Forbidden) return false;
        var finalPath = response.RequestMessage?.RequestUri?.AbsolutePath ?? string.Empty;
        return !finalPath.Contains("/AccessDenied", StringComparison.OrdinalIgnoreCase)
            && !finalPath.Contains("/Auth/Login", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Posts a form after fetching a fresh antiforgery token from <paramref name="tokenPath"/>.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(
        string tokenPath, string action, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var token = await GetTokenAsync(tokenPath);
        var payload = new List<KeyValuePair<string, string>>(fields)
        {
            new("__RequestVerificationToken", token)
        };
        return await _http.PostAsync(action, new FormUrlEncodedContent(payload));
    }

    public Task<HttpResponseMessage> PostFormAsync(string tokenPath, string action, IDictionary<string, string> fields) =>
        PostFormAsync(tokenPath, action, fields.AsEnumerable());

    /// <summary>
    /// Posts a form <em>without</em> an antiforgery token, to prove the endpoint rejects a
    /// cross-site request. Used by the CSRF regression tests.
    /// </summary>
    public async Task<HttpResponseMessage> PostWithoutTokenAsync(
        string action, IEnumerable<KeyValuePair<string, string>> fields) =>
        await _http.PostAsync(action, new FormUrlEncodedContent(fields));

    /// <summary>Posts a multipart form (a file plus fields) after fetching a fresh antiforgery token.</summary>
    public async Task<HttpResponseMessage> PostMultipartAsync(
        string tokenPath,
        string action,
        IEnumerable<KeyValuePair<string, string>> fields,
        string fileField,
        string fileName,
        byte[] content,
        string contentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")
    {
        var token = await GetTokenAsync(tokenPath);
        using var form = new MultipartFormDataContent();
        foreach (var field in fields)
            form.Add(new StringContent(field.Value), field.Key);
        form.Add(new StringContent(token), "__RequestVerificationToken");

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, fileField, fileName);

        return await _http.PostAsync(action, form);
    }

    /// <summary>Posts a form carrying several files, each under its own field name.</summary>
    public async Task<HttpResponseMessage> PostMultipartAsync(
        string tokenPath,
        string action,
        IEnumerable<KeyValuePair<string, string>> fields,
        IEnumerable<(string Field, string FileName, byte[] Content, string ContentType)> files)
    {
        var token = await GetTokenAsync(tokenPath);
        using var form = new MultipartFormDataContent();
        foreach (var field in fields)
            form.Add(new StringContent(field.Value), field.Key);
        form.Add(new StringContent(token), "__RequestVerificationToken");

        foreach (var (field, fileName, content, contentType) in files)
        {
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(file, field, fileName);
        }

        return await _http.PostAsync(action, form);
    }
}
