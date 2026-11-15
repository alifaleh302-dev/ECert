using System.Net;
using System.Text.RegularExpressions;
using ECert.Models;
using ECert.Services;
using ECert.Tests.Infrastructure;
using Xunit;

namespace ECert.Tests;

/// <summary>
/// Guards the single feedback channel: a server flash message must be queued once (rendered as a
/// toast by notify.js), never duplicated by a page that also reads TempData itself, and the toast
/// host must exist with the z-index token that keeps it above every overlay.
/// </summary>
[Collection(AppCollection.Name)]
public class NotificationTests
{
    private readonly AppFixture _fixture;

    public NotificationTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Flash_message_is_queued_once_and_not_rendered_twice()
    {
        // Regression: the layout and 13 views both rendered TempData, so a success message
        // appeared twice on the page.
        var admin = await SignInAdminAsync();
        var designId = await CreateDesignAsync("قالب لاختبار الإشعار");

        var response = await admin.PostFormAsync("/CertificateDesigns",
            $"/CertificateDesigns/Rename/{designId}",
            new Dictionary<string, string> { ["name"] = "قالب أُعيدت تسميته" });

        var html = System.Net.WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        // Exactly one queue is emitted, and it carries the message.
        Assert.Single(Regex.Matches(html, "window\\.__ecertFlash"));
        Assert.Contains("تمت إعادة تسمية القالب", html);

        // The success alert may only appear inside the <noscript> fallback, never as a second
        // visible banner. CertificateDesigns/Index used to render its own copy.
        var noscript = Regex.Match(html, "<noscript>(.*?)</noscript>", RegexOptions.Singleline);
        var outsideNoscript = html.Replace(noscript.Value, string.Empty);
        Assert.DoesNotContain("alert alert-success", outsideNoscript);
    }

    [Fact]
    public async Task Every_page_carries_the_shared_toast_host_and_z_index_token()
    {
        var admin = await SignInAdminAsync();

        foreach (var path in new[] { "/Dashboard", "/CertificateDesigns", "/Cashbox" })
        {
            var html = await admin.GetStringAsync(path);
            Assert.Contains("notify.js", html);
        }

        var css = await admin.GetStringAsync("/css/admin.css");
        Assert.Contains(".toast-host", css);
        Assert.Contains("--z-toast", css);
        // The toast layer must sit above the designer's fullscreen overlay.
        var toastZ = int.Parse(Regex.Match(css, @"--z-toast:\s*(\d+)").Groups[1].Value);
        var designerZ = int.Parse(Regex.Match(css, @"--z-designer:\s*(\d+)").Groups[1].Value);
        Assert.True(toastZ > designerZ, "the toast host must sit above the designer overlay");
    }

    [Fact]
    public async Task A_page_without_a_flash_renders_no_alert_banner()
    {
        // Cashbox/Index used to render its own TempData banner. A plain visit carries no flash,
        // so the page must show nothing at all — no queue and no banner.
        var admin = await SignInAdminAsync();
        var html = await admin.GetStringAsync("/Cashbox");
        Assert.DoesNotContain("window.__ecertFlash", html);
        Assert.DoesNotContain("a-note--success", html);
        Assert.DoesNotContain("a-note--danger", html);
    }

    private async Task<AppClient> SignInAdminAsync()
    {
        var client = AppClient.Create(_fixture.Factory);
        var landing = await client.LoginAsync("admin", TestAppFactory.AdminPassword);
        Assert.Equal("/Dashboard", landing);
        return client;
    }

    private async Task<int> CreateDesignAsync(string name)
    {
        return await _fixture.Factory.WithDbAsync(async db =>
        {
            var design = new CertificateDesign
            {
                Name = name,
                DesignKey = $"flash-{Guid.NewGuid():N}",
                IsPublished = false,
                UpdatedBy = "test"
            };
            db.CertificateDesigns.Add(design);
            await db.SaveChangesAsync();
            return design.CertificateDesignId;
        });
    }
}
